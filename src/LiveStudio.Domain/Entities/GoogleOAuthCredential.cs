namespace LiveStudio.Domain.Entities;

/// <summary>Encrypted Google OAuth token payload for the starter's single connected account.</summary>
public sealed class GoogleOAuthCredential
{
    public string Key { get; set; } = "default";
    public string ProtectedTokenPayload { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
