using System.IO;
using System.Text.Json;
using HsModManager.Models;

namespace HsModManager.Services;

public sealed class SkinCatalogCacheService
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public string CachePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HsModManager",
        "skin-catalog.json");

    public async Task<SkinCatalogCache?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(CachePath))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(CachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            SkinCatalogCache? cache = await JsonSerializer.DeserializeAsync<SkinCatalogCache>(stream, _jsonOptions, cancellationToken);
            return cache?.Version == 1 ? cache : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SaveAsync(SkinCatalog catalog, CancellationToken cancellationToken = default)
    {
        string? directory = Path.GetDirectoryName(CachePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = CachePath + ".tmp";
        try
        {
            var cache = new SkinCatalogCache
            {
                UpdatedAt = DateTime.Now,
                Catalog = catalog
            };

            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, cache, _jsonOptions, cancellationToken);
            }

            File.Move(temporaryPath, CachePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
