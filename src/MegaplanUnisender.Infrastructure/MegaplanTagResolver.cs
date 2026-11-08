using MegaplanUnisender.Core;

namespace MegaplanUnisender.Infrastructure;

public static class MegaplanTagResolver
{
    public static async Task ResolveAsync(MegaplanOptions options, IMegaplanClient client, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.EligibilityGlobalTagName) && !options.SyncAllGlobalTags) return;
        var tags = await client.GetGlobalTagsAsync(ct);
        var byName = !string.IsNullOrWhiteSpace(options.EligibilityGlobalTagName);
        var matches = tags.Where(x => x.IsGlobal && (byName
            ? string.Equals(x.Name, options.EligibilityGlobalTagName.Trim(), StringComparison.Ordinal)
            : x.Id == options.EligibilityGlobalTagId)).ToArray();
        if (matches.Length == 0)
            throw new InvalidOperationException("Глобальная метка не найдена. Проверьте Settings.Megaplan.EligibilityGlobalTagName и доступ приложения к меткам.");
        if (matches.Length != 1)
            throw new InvalidOperationException("Найдено несколько глобальных меток с таким именем. Задайте уникальное EligibilityGlobalTagName или используйте настройку по ID.");

        var tag = matches[0];
        options.EligibilityGlobalTagId = tag.Id;
        var managed = options.SyncAllGlobalTags ? tags.Where(x => x.IsGlobal).Select(x => x.Id) : [tag.Id];
        options.ManagedGlobalTagIds = options.ManagedGlobalTagIds.Concat(managed).Distinct(StringComparer.Ordinal).ToArray();
    }
}
