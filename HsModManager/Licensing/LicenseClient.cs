using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;

namespace HsModManager.Licensing;

public static class LicenseClient
{
    public static readonly TimeSpan ValidationTimeout = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan ValidationRefreshInterval = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static string GetServerUrl()
    {
        // Keep the fixed endpoint out of UI, configuration files and logs.
        byte[] address = [43, 139, 90, 166];
        return $"http://{string.Join('.', address)}";
    }

    internal static string SanitizeUserMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "";
        }

        string sanitized = message;
        string serverUrl = GetServerUrl();
        sanitized = sanitized.Replace(serverUrl, "授权服务", StringComparison.OrdinalIgnoreCase);
        if (Uri.TryCreate(serverUrl, UriKind.Absolute, out Uri? serverUri))
        {
            sanitized = sanitized.Replace(serverUri.Host, "授权服务", StringComparison.OrdinalIgnoreCase);
        }

        return sanitized;
    }

    public static async Task<LicenseSession> ValidateAsync(
        HttpClient httpClient,
        string licenseKey,
        CancellationToken cancellationToken)
    {
        string serverUrl = GetServerUrl();
        string normalizedKey = LicenseCredentialStore.NormalizeKey(licenseKey);
        if (normalizedKey.Length != 8)
        {
            return CreateFailedSession(serverUrl, normalizedKey, "invalid_format", "卡密必须是 8 位数字或字母。");
        }

        try
        {
            var request = new ClientValidateRequest(
                normalizedKey,
                MachineFingerprint.Create(),
                Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown");
            using HttpResponseMessage response = await httpClient.PostAsJsonAsync(
                $"{serverUrl}/api/client/license/validate",
                request,
                JsonOptions,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return CreateFailedSession(
                    serverUrl,
                    normalizedKey,
                    "server_error",
                    $"卡密验证失败：HTTP {(int)response.StatusCode}");
            }

            ClientValidateResponse? result = await response.Content.ReadFromJsonAsync<ClientValidateResponse>(
                JsonOptions,
                cancellationToken);
            if (result == null)
            {
                return CreateFailedSession(serverUrl, normalizedKey, "bad_response", "卡密验证响应为空。");
            }

            return new LicenseSession(
                serverUrl,
                normalizedKey,
                result.Valid,
                result.Status ?? "",
                SanitizeUserMessage(result.Message),
                result.ExpiresAtUtc,
                result.ServerTimeUtc,
                DateTime.UtcNow);
        }
        catch (OperationCanceledException)
        {
            return CreateFailedSession(serverUrl, normalizedKey, "timeout", "卡密验证超时，请检查网络后重试。");
        }
        catch (Exception)
        {
            return CreateFailedSession(serverUrl, normalizedKey, "network_error", "卡密验证失败，请检查网络后重试。");
        }
    }

    private static LicenseSession CreateFailedSession(
        string serverUrl,
        string licenseKey,
        string status,
        string message)
    {
        DateTime now = DateTime.UtcNow;
        return new LicenseSession(serverUrl, licenseKey, false, status, message, null, now, now);
    }

    private sealed record ClientValidateRequest(
        string LicenseKey,
        string MachineFingerprint,
        string AppVersion);

    private sealed record ClientValidateResponse(
        bool Valid,
        string? Status,
        string? Message,
        string? LicenseKey,
        DateTime? ExpiresAtUtc,
        DateTime ServerTimeUtc);
}
