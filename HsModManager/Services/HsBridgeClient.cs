using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;
using HsModManager.Models;

namespace HsModManager.Services;

public sealed class HsBridgeClient : IDisposable
{
    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public const string Host = "127.0.0.1";
    public const int Port = 58744;
    public Uri BaseUri => new($"http://{Host}:{Port}/");

    public async Task<StatusResponse?> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        return await GetJsonAsync<StatusResponse>("api/status", cancellationToken);
    }

    public async Task<ConfigMetadataResponse?> GetConfigAsync(string language = "zhCN", CancellationToken cancellationToken = default)
    {
        return await GetJsonAsync<ConfigMetadataResponse>($"api/config?lang={Uri.EscapeDataString(language)}", cancellationToken);
    }

    public async Task<SkinCatalogResponse?> GetSkinsAsync(CancellationToken cancellationToken = default)
    {
        return await GetJsonAsync<SkinCatalogResponse>("api/skins", cancellationToken);
    }

    public async Task<SkinSettingsResponse?> GetSkinSettingsAsync(CancellationToken cancellationToken = default)
    {
        return await GetJsonAsync<SkinSettingsResponse>("api/skin-settings", cancellationToken);
    }

    public async Task<ApiResult> SaveConfigAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        string apiKey = key.EndsWith(".name", StringComparison.OrdinalIgnoreCase) ? key : $"{key}.name";
        return await PostJsonAsync("config", new Dictionary<string, string>
        {
            ["key"] = apiKey,
            ["value"] = value
        }, cancellationToken);
    }

    public async Task<ApiResult> UpdateHsSkinsAsync(string content, CancellationToken cancellationToken = default)
    {
        return await PostJsonAsync("update", new Dictionary<string, string>
        {
            ["key"] = "hsskins.cfg",
            ["value"] = content
        }, cancellationToken);
    }

    public async Task<ApiResult> RunActionAsync(string action, CancellationToken cancellationToken = default)
    {
        return await PostJsonAsync("api/action", new Dictionary<string, string>
        {
            ["action"] = action
        }, cancellationToken);
    }

    public async Task<string> GetTextAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await _http.GetAsync(new Uri(BaseUri, relativePath.TrimStart('/')), cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task<T?> GetJsonAsync<T>(string relativePath, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.GetAsync(new Uri(BaseUri, relativePath.TrimStart('/')), cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, cancellationToken);
    }

    private async Task<ApiResult> PostJsonAsync(string relativePath, object body, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(body, _jsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _http.PostAsync(new Uri(BaseUri, relativePath.TrimStart('/')), content, cancellationToken);
        string responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        ApiResult? result = null;
        try
        {
            result = JsonSerializer.Deserialize<ApiResult>(responseText, _jsonOptions);
        }
        catch
        {
            result = new ApiResult
            {
                Status = (int)response.StatusCode,
                Output = responseText
            };
        }

        result ??= new ApiResult { Status = (int)response.StatusCode };
        if (result.Status == 0)
        {
            result.Status = (int)response.StatusCode;
        }

        if (!response.IsSuccessStatusCode && string.IsNullOrWhiteSpace(result.Error) && string.IsNullOrWhiteSpace(result.Output))
        {
            result.Error = response.ReasonPhrase;
        }

        return result;
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
