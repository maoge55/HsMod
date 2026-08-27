namespace HsModManager.Licensing;

public static class LocalDevelopmentLicense
{
    private const string LocalServerUrl = "local-test-machine";
    private const string LocalLicenseKey = "LOCALDEV";
    private const string LocalStatus = "local_test_machine";
    private const string AllowedMachineFingerprint = "4A177335A055D1BDE744ACB88CED0C99";

    public static bool TryCreateSession(out LicenseSession? session)
    {
        session = null;
        if (!string.Equals(
                MachineFingerprint.Create(),
                AllowedMachineFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        DateTime now = DateTime.UtcNow;
        session = new LicenseSession(
            LocalServerUrl,
            LocalLicenseKey,
            true,
            LocalStatus,
            "Local test machine mode is enabled.",
            new DateTime(9999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            now,
            now);
        return true;
    }
}
