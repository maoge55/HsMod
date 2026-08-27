namespace HsModManager.Licensing;

public sealed record LicenseSession(
    string ServerUrl,
    string LicenseKey,
    bool Valid,
    string Status,
    string Message,
    DateTime? ExpiresAtUtc,
    DateTime ServerTimeUtc,
    DateTime CheckedAtUtc);
