namespace MegaplanUnisender.Core;

// The Secrets section comes from .NET User Secrets or environment variables, independently of Settings.
public sealed class Secrets
{
    public string MegaplanApiToken { get; set; } = "";
    public string UnisenderApiKey { get; set; } = "";
    public string WebhookSecret { get; set; } = "";
    public string GraylogAddress { get; set; } = "";
    public int GraylogPort { get; set; } = 12201;
    public string GraylogApplicationName { get; set; } = "megaplan-unisender-sync";
    public string GraylogSource { get; set; } = "";
    public string GraylogMinimumLevel { get; set; } = "Information";
    public bool HasUnisenderApiKey => !Missing(UnisenderApiKey);

    public IEnumerable<string> Validate(IntegrationOptions settings)
    {
        if (!settings.Sync.Enabled) yield break;
        if (Missing(MegaplanApiToken)) yield return "Fill Secrets:MegaplanApiToken before enabling Sync";
        if (!settings.Sync.DryRun && !HasUnisenderApiKey) yield return "Fill Secrets:UnisenderApiKey before enabling writes";
        if (WebhookSecret.Length < 32 || Missing(WebhookSecret))
            yield return "Secrets:WebhookSecret requires a random value of at least 32 characters";
    }
    private static bool Missing(string value) => string.IsNullOrWhiteSpace(value) || value.Contains("CHANGE", StringComparison.OrdinalIgnoreCase);
}
