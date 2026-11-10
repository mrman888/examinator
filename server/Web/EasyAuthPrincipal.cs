using System.Text.Json;
using System.Text.Json.Serialization;

namespace Examinator.Web;

public static class EasyAuthPrincipal
{
    public static bool IsAllowed(string encodedPrincipal, string tenantId, IReadOnlyCollection<string> allowedUserIds)
    {
        if (string.IsNullOrWhiteSpace(encodedPrincipal) || encodedPrincipal.Length > 32_768) return false;
        try
        {
            var principal = JsonSerializer.Deserialize<Principal>(Convert.FromBase64String(encodedPrincipal));
            if (principal?.Claims is null || principal.AuthenticationType != "aad") return false;
            var tenant = principal.Claims.SingleOrDefault(claim =>
                claim.Type is "tid" or "http://schemas.microsoft.com/identity/claims/tenantid")?.Value;
            var user = principal.Claims.SingleOrDefault(claim =>
                claim.Type is "oid" or "http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;
            return string.Equals(tenant, tenantId, StringComparison.OrdinalIgnoreCase) &&
                user is not null && allowedUserIds.Contains(user, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException)
        {
            // Invalid or ambiguous identity claims are rejected, never treated as anonymous access.
            return false;
        }
    }

    private sealed record Principal(
        [property: JsonPropertyName("auth_typ")] string? AuthenticationType,
        [property: JsonPropertyName("claims")] Claim[]? Claims);
    private sealed record Claim(
        [property: JsonPropertyName("typ")] string? Type,
        [property: JsonPropertyName("val")] string? Value);
}
