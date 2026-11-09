namespace MegaplanUnisender.Core;

public sealed class IntegrationOptions
{
    public MegaplanOptions Megaplan { get; set; } = new();
    public UnisenderOptions Unisender { get; set; } = new();
    public WebhookOptions Webhook { get; set; } = new();
    public SyncOptions Sync { get; set; } = new();
    public DiagnosticsOptions Diagnostics { get; set; } = new();

    public IEnumerable<string> Validate()
    {
        if (Webhook.MaxBodySize is < 1024 or > 1048576) yield return "Webhook:MaxBodySize must be 1024..1048576";
        if (Sync.PollingIntervalMinutes is < 0 or > 43200) yield return "Sync:PollingIntervalMinutes must be 0..43200";
        if (Sync.MaxRetries is < 0 or > 20 || Sync.RetryBackoff <= TimeSpan.Zero) yield return "Invalid Sync retry settings";
        if (Sync.QueueCapacity is < 1 or > 4096) yield return "QueueCapacity must be 1..4096";
        if (Sync.RecentContractorCacheCapacity is < 1 or > 4096 || Sync.RecentContractorCacheTtl <= TimeSpan.Zero ||
            Sync.RecentContractorCacheTtl > TimeSpan.FromMinutes(30)) yield return "Invalid short-term contractor cache limits";
        if (Sync.RateLimits.GeneralPerMinute is < 1 or > 1200 || Sync.RateLimits.SubscribePerMinute is < 1 or > 300 ||
            Sync.RateLimits.MegaplanPerMinute is < 1 or > 1200 || Sync.RateLimits.ExportPerMinute is < 1 or > 20)
            yield return "Invalid rate limits";
        if (Megaplan.AccountIdentityField is not ("uniqueAccountId" or "permanentHost" or "id")) yield return "Unsupported account identity field";
        if (Unisender.VerifiedMissingContactErrorCodes.Any(x => x is "unisender_unspecified" or "unisender_invalid_api_key" or "unisender_access_denied" or "unisender_invalid_arg" or "unisender_retry_later" or "unisender_api_call_limit_exceeded_for_api_key" or "unisender_api_call_limit_exceeded_for_ip"))
            yield return "Generic/auth/rate-limit errors cannot prove that a contact is missing";
        if (!string.IsNullOrEmpty(Unisender.ListId) && (!long.TryParse(Unisender.ListId, out var list) || list <= 0)) yield return "ListId must be one positive numeric ID";
        if (!Enum.IsDefined(Unisender.TagOwnershipMode) || !Enum.IsDefined(Unisender.ConsentPolicy) ||
            Unisender.DoubleOptInMode is not (0 or 3 or 4)) yield return "Invalid Unisender policy";
        if (Unisender.TagOwnershipMode == TagOwnershipMode.Exclusive && !Unisender.ConfirmOverwriteAllTags)
            yield return "Exclusive requires Unisender:ConfirmOverwriteAllTags=true";
        if (Unisender.DoubleOptInMode is 0 or 4 && !Unisender.AllowConfirmationEmails)
            yield return "double_optin=0/4 requires AllowConfirmationEmails=true";
        if (Unisender.DoubleOptInMode == 3 && Unisender.ConsentPolicy != ConsentPolicy.VerifiedByAdministrator && Sync.Enabled && !Sync.DryRun)
            yield return "double_optin=3 requires verified consent policy";
        if (Unisender.ConsentPolicy == ConsentPolicy.RequestConfirmation && Unisender.DoubleOptInMode == 3) yield return "RequestConfirmation requires double_optin=0 or 4";
        if (Megaplan.TagMappings.Values.Any(x => string.IsNullOrWhiteSpace(x) || x.Contains(',') || x.Length > 100))
            yield return "Mapped tag names must be nonempty, <=100 chars and contain no comma";
        if (Megaplan.TagMappings.Keys.Any(x => !Megaplan.ManagedGlobalTagIds.Contains(x)))
            yield return "Every mapped tag ID must be in ManagedGlobalTagIds";
        if (Megaplan.EligibilityGlobalTagName.Length > 100)
            yield return "Megaplan:EligibilityGlobalTagName must be at most 100 characters";
        if (Unisender.FieldMappings.Keys.Any(x => x is not ("name" or "phone") && !x.StartsWith("extra:", StringComparison.Ordinal)) ||
            Unisender.FieldMappings.Values.Any(x => string.IsNullOrWhiteSpace(x) || x is "email" or "tags" || x.Contains('[') || x.Contains(']')))
            yield return "FieldMappings permits name, phone, extra:<field>; destination cannot be email/tags";
        if (!Sync.Enabled) yield break;
        if (!HttpsOrigin(Megaplan.BaseUrl) || !HttpsBase(Unisender.ApiBaseUrl)) yield return "API base URLs must use HTTPS without credentials or query";
        if (Placeholder(Megaplan.ExpectedIntegrationUuid))
            yield return "Заполните Settings:Megaplan:ExpectedIntegrationUuid — UUID созданного приложения";
        if (!string.IsNullOrEmpty(Megaplan.ExpectedAccountId) && Placeholder(Megaplan.ExpectedAccountId))
            yield return "Уберите заглушку ExpectedAccountId: аккаунт определяется через API автоматически";
        if (string.IsNullOrWhiteSpace(Megaplan.EligibilityGlobalTagName))
        {
            if (Placeholder(Megaplan.EligibilityGlobalTagId)) yield return "Set Megaplan:EligibilityGlobalTagName or EligibilityGlobalTagId";
            if (!Megaplan.ManagedGlobalTagIds.Contains(Megaplan.EligibilityGlobalTagId)) yield return "Eligibility ID must be an approved global ID";
        }
        if (!Sync.DryRun && Unisender.ConsentPolicy == ConsentPolicy.Disabled)
            yield return "Для записи выберите Unisender:ConsentPolicy; для проверки без записи оставьте Sync:DryRun=true";
        if (!Sync.DryRun && Unisender.TagOwnershipMode == TagOwnershipMode.PreserveExternal)
            yield return "Для точной записи тегов требуется Unisender:TagOwnershipMode=Exclusive и ConfirmOverwriteAllTags=true: теги контакта будут принадлежать интеграции";
    }
    private static bool Placeholder(string x) => string.IsNullOrWhiteSpace(x) || x.Contains("CHANGE", StringComparison.OrdinalIgnoreCase);
    private static bool HttpsBase(string x) => Uri.TryCreate(x, UriKind.Absolute, out var u) && u.Scheme == "https" && u.UserInfo == "" && u.Query == "" && u.Fragment == "";
    private static bool HttpsOrigin(string x) => HttpsBase(x) && new Uri(x).AbsolutePath == "/";
}
public sealed class MegaplanOptions
{
    public string BaseUrl { get; set; } = "https://CHANGE-ME.megaplan.ru";
    public string ExpectedAccountId { get; set; } = "";
    public string AccountIdentityField { get; set; } = "permanentHost";
    public string ExpectedIntegrationUuid { get; set; } = "";
    public string EligibilityGlobalTagName { get; set; } = "";
    public string EligibilityGlobalTagId { get; set; } = "";
    public bool SyncAllGlobalTags { get; set; } = true;
    public string[] ManagedGlobalTagIds { get; set; } = [];
    public Dictionary<string, string> TagMappings { get; set; } = [];
    public string[] ContractorModels { get; set; } = ["Contractor", "ContractorHuman", "ContractorCompany"];
}
public enum TagOwnershipMode { PreserveExternal, Exclusive }
public enum ConsentPolicy { Disabled, VerifiedByAdministrator, RequestConfirmation }
public sealed class UnisenderOptions
{
    public string ApiBaseUrl { get; set; } = "https://api.unisender.com/ru/api/";
    public string ListId { get; set; } = "";
    public string ListName { get; set; } = "";
    public TagOwnershipMode TagOwnershipMode { get; set; }
    public bool ConfirmOverwriteAllTags { get; set; }
    public int DoubleOptInMode { get; set; } = 3;
    public ConsentPolicy ConsentPolicy { get; set; }
    public bool AllowConfirmationEmails { get; set; }
    public bool ConfirmEmptyTagsClear { get; set; } = true;
    public string[] VerifiedMissingContactErrorCodes { get; set; } = [];
    public bool ConfirmNullResultMeansMissing { get; set; }
    public Dictionary<string, string> FieldMappings { get; set; } = [];
}
public sealed class WebhookOptions { public int MaxBodySize { get; set; } = 65536; }
public sealed class DiagnosticsOptions { public bool EnableLiveTests { get; set; } public string TestEmail { get; set; } = ""; }
public sealed class SyncOptions
{
    public bool Enabled { get; set; }
    public bool DryRun { get; set; } = true;
    public int PollingIntervalMinutes { get; set; }
    public int QueueCapacity { get; set; } = 256;
    public int RecentContractorCacheCapacity { get; set; } = 256;
    public TimeSpan RecentContractorCacheTtl { get; set; } = TimeSpan.FromMinutes(5);
    public int MaxRetries { get; set; } = 6;
    public TimeSpan RetryBackoff { get; set; } = TimeSpan.FromSeconds(10);
    public RateLimitOptions RateLimits { get; set; } = new();
}
public sealed class RateLimitOptions
{
    public int GeneralPerMinute { get; set; } = 120;
    public int SubscribePerMinute { get; set; } = 60;
    public int ExportPerMinute { get; set; } = 20;
    public int MegaplanPerMinute { get; set; } = 60;
}
