namespace Examinator.Web;

public sealed class GatewaySettings
{
    public string TenantId { get; }
    public string ApiClientId { get; }
    public Uri ApiBaseUrl { get; }
    public string PublicOrigin { get; }
    public string[] AllowedUserIds { get; }
    public bool LocalAnonymous { get; }

    public GatewaySettings(IConfiguration configuration, bool localAnonymous)
    {
        LocalAnonymous = localAnonymous;
        TenantId = configuration["Azure:TenantId"] ?? "";
        ApiClientId = configuration["Azure:ApiClientId"] ?? "";
        AllowedUserIds = configuration.GetSection("Gateway:AllowedUserIds").Get<string[]>() ?? [];
        PublicOrigin = configuration["Gateway:PublicOrigin"] ?? "";
        if (!Uri.TryCreate(configuration["Gateway:ApiBaseUrl"], UriKind.Absolute, out var apiUri) ||
            (apiUri.Scheme != "https" && !(localAnonymous && apiUri.IsLoopback && apiUri.Scheme == "http")))
            throw new InvalidOperationException("Configure a valid HTTPS Gateway:ApiBaseUrl (HTTP loopback allowed for local development).");
        ApiBaseUrl = apiUri;
        if (!localAnonymous && (!Guid.TryParse(TenantId, out _) || !Guid.TryParse(ApiClientId, out _) ||
            AllowedUserIds.Length == 0 || AllowedUserIds.Any(id => !Guid.TryParse(id, out _)) ||
            !Uri.TryCreate(PublicOrigin, UriKind.Absolute, out var origin) || origin.Scheme != "https" ||
            origin.GetLeftPart(UriPartial.Authority) != PublicOrigin))
            throw new InvalidOperationException("Configure the tenant, API client ID, approved user object IDs and exact HTTPS website origin.");
    }
}
