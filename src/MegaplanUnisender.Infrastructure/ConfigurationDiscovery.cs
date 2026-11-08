using System.Text.Json;
using MegaplanUnisender.Core;

namespace MegaplanUnisender.Infrastructure;

public static class ConfigurationDiscovery
{
    public static async Task ResolveAsync(IntegrationOptions options, IMegaplanClient megaplan, IUnisenderClient unisender, CancellationToken ct, bool unisenderConfigured = true)
    {
        if (!options.Sync.Enabled) return;
        if (string.IsNullOrWhiteSpace(options.Megaplan.ExpectedAccountId))
            options.Megaplan.ExpectedAccountId = await megaplan.GetAccountIdentityAsync(options.Megaplan.AccountIdentityField, ct);
        await MegaplanTagResolver.ResolveAsync(options.Megaplan, megaplan, ct);
        if (options.Sync.DryRun && !unisenderConfigured) return;
        await ResolveListAsync(options.Unisender, unisender, allowCreate: !options.Sync.DryRun, ct);
        if (!options.Sync.DryRun) await unisender.VerifyMissingContactResponseAsync(ct);
    }

    public static async Task ResolveListAsync(UnisenderOptions options, IUnisenderClient client, bool allowCreate, CancellationToken ct)
    {
        var catalog = await client.ReadAsync("getLists", ct);
        var lists = ParseLists(catalog);
        var name = options.ListName.Trim();
        if (string.IsNullOrWhiteSpace(options.ListId) && name.Length > 0 && !lists.Any(x => x.Name == name))
        {
            if (!allowCreate) throw new InvalidOperationException("Список Unisender не найден. В dry-run список не создаётся; при запуске с DryRun=false он будет создан автоматически по ListName.");
            string id;
            try { id = await client.CreateListAsync(name, ct); }
            catch (SyncException)
            {
                // A concurrent start or lost response may already have created the unique title. Read once; never blindly repeat creation.
                var after = await client.ReadAsync("getLists", ct);
                if (!ParseLists(after).Any(x => x.Name == name)) throw;
                ResolveList(options, after);
                return;
            }
            options.ListId = id;
            options.ListName = name;
            return;
        }
        ResolveList(options, catalog);
    }
    public static void ResolveList(UnisenderOptions options, JsonElement catalog)
    {
        var lists = ParseLists(catalog);
        var hasId = !string.IsNullOrWhiteSpace(options.ListId);
        var hasName = !string.IsNullOrWhiteSpace(options.ListName);
        var matches = lists.Where(x => (!hasId || x.Id == options.ListId) &&
            (!hasName || string.Equals(x.Name, options.ListName.Trim(), StringComparison.Ordinal))).ToArray();
        if (matches.Length != 1)
        {
            // Only list names are needed for setup. Never include raw vendor responses or credentials.
            var names = JsonSerializer.Serialize(lists.Select(x => x.Name).Take(50));
            throw new InvalidOperationException("Не удалось однозначно выбрать список Unisender. Укажите Settings:Unisender:ListName — точное название существующего списка. " +
                "Если список один, ListName можно оставить пустым. Доступные списки: " + names);
        }
        options.ListId = matches[0].Id!;
        options.ListName = matches[0].Name!;
    }
    private static (string? Id, string? Name)[] ParseLists(JsonElement catalog)
    {
        if (catalog.ValueKind != JsonValueKind.Array) throw new SyncException("unisender_list_catalog_invalid");
        var lists = catalog.EnumerateArray().Select(x => (Id: x.Text("id"), Name: x.Text("title"))).ToArray();
        if (lists.Any(x => !long.TryParse(x.Id, out var id) || id <= 0 || string.IsNullOrWhiteSpace(x.Name)) ||
            lists.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != lists.Length)
            throw new SyncException("unisender_list_catalog_invalid");
        return lists;
    }
}
