using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace HsModManager.Models;

public sealed class ApiResult
{
    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("output")]
    public string? Output { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    public bool IsSuccess => Status >= 200 && Status < 300 && string.IsNullOrWhiteSpace(Error);
}

public sealed class StatusResponse
{
    [JsonPropertyName("plugin")]
    public PluginStatus Plugin { get; set; } = new();

    [JsonPropertyName("game")]
    public GameStatus Game { get; set; } = new();

    [JsonPropertyName("web")]
    public WebStatus Web { get; set; } = new();

    [JsonPropertyName("paths")]
    public PathStatus Paths { get; set; } = new();

    [JsonPropertyName("patches")]
    public List<PatchStatus> Patches { get; set; } = [];
}

public sealed class PluginStatus
{
    [JsonPropertyName("guid")]
    public string Guid { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("language")]
    public string Language { get; set; } = "";

    [JsonPropertyName("runningTime")]
    public long RunningTime { get; set; }
}

public sealed class GameStatus
{
    [JsonPropertyName("pid")]
    public int Pid { get; set; }

    [JsonPropertyName("login")]
    public bool Login { get; set; }

    [JsonPropertyName("hsunitid")]
    public string HsUnitId { get; set; } = "";

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "";
}

public sealed class WebStatus
{
    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("root")]
    public string Root { get; set; } = "";
}

public sealed class PathStatus
{
    [JsonPropertyName("gameRoot")]
    public string GameRoot { get; set; } = "";

    [JsonPropertyName("bepInExRoot")]
    public string BepInExRoot { get; set; } = "";

    [JsonPropertyName("config")]
    public string Config { get; set; } = "";

    [JsonPropertyName("hsMatchLog")]
    public string HsMatchLog { get; set; } = "";
}

public sealed class PatchStatus
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("methodCount")]
    public int MethodCount { get; set; }

    public string Display => $"{Name} ({MethodCount})";
}

public sealed class ConfigMetadataResponse
{
    [JsonPropertyName("language")]
    public string Language { get; set; } = "";

    [JsonPropertyName("groups")]
    public Dictionary<string, List<ConfigItem>> Groups { get; set; } = [];
}

public sealed class ConfigItem : INotifyPropertyChanged
{
    private string _value = "";
    private bool _isSaving;

    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("value")]
    public string Value
    {
        get => _value;
        set => SetField(ref _value, value);
    }

    [JsonPropertyName("isAdvanced")]
    public bool IsAdvanced { get; set; }

    [JsonPropertyName("enumValues")]
    public string[] EnumValues { get; set; } = [];

    [JsonPropertyName("keyCodes")]
    public string[] KeyCodes { get; set; } = [];

    [JsonPropertyName("min")]
    public double? Min { get; set; }

    [JsonPropertyName("max")]
    public double? Max { get; set; }

    [JsonIgnore]
    public bool IsSaving
    {
        get => _isSaving;
        set => SetField(ref _isSaving, value);
    }

    [JsonIgnore]
    public string DisplayName => $"{Name} ({Key})";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class SkinCatalogResponse
{
    [JsonPropertyName("current")]
    public Dictionary<string, int> Current { get; set; } = [];

    [JsonPropertyName("catalog")]
    public SkinCatalog Catalog { get; set; } = new();

    [JsonPropertyName("hsskins")]
    public string HsSkins { get; set; } = "";
}

public sealed class SkinSettingsResponse
{
    [JsonPropertyName("current")]
    public Dictionary<string, int> Current { get; set; } = [];

    [JsonPropertyName("hsskins")]
    public string HsSkins { get; set; } = "";
}

public sealed class SkinCatalogCache
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    [JsonPropertyName("catalog")]
    public SkinCatalog Catalog { get; set; } = new();
}

public sealed class SkinCatalog
{
    [JsonPropertyName("coins")]
    public List<SkinItem> Coins { get; set; } = [];

    [JsonPropertyName("cardBacks")]
    public List<SkinItem> CardBacks { get; set; } = [];

    [JsonPropertyName("boards")]
    public List<SkinItem> Boards { get; set; } = [];

    [JsonPropertyName("battlegroundBoards")]
    public List<SkinItem> BattlegroundBoards { get; set; } = [];

    [JsonPropertyName("battlegroundFinishers")]
    public List<SkinItem> BattlegroundFinishers { get; set; } = [];

    [JsonPropertyName("heroes")]
    public List<SkinItem> Heroes { get; set; } = [];

    [JsonPropertyName("battlegroundHeroes")]
    public List<SkinItem> BattlegroundHeroes { get; set; } = [];

    [JsonPropertyName("bobs")]
    public List<SkinItem> Bobs { get; set; } = [];

    [JsonPropertyName("pets")]
    public List<SkinItem> Pets { get; set; } = [];
}

public sealed class SkinItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("extra")]
    public string Extra { get; set; } = "";

    [JsonPropertyName("heroClass")]
    public string HeroClass { get; set; } = "";

    [JsonIgnore]
    public string Display => Name;
}

public sealed class ClassSkinSelection : INotifyPropertyChanged
{
    private int _selectedId = -1;

    public string ClassCode { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public List<SkinItem> Options { get; init; } = [];
    public bool IsAvailable => Options.Any(item => item.Id > 0);

    public int SelectedId
    {
        get => _selectedId;
        set
        {
            if (_selectedId == value)
            {
                return;
            }

            _selectedId = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedId)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class InstallCandidate
{
    public string Path { get; set; } = "";
    public string Source { get; set; } = "";

    public override string ToString() => $"{Path}  [{Source}]";
}

public sealed class InstallResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public string HearthstonePath { get; init; } = "";
}

public sealed class LogEntry
{
    public DateTime Time { get; init; } = DateTime.Now;
    public string Level { get; init; } = "INFO";
    public string Message { get; init; } = "";
    public string Display => $"[{Time:HH:mm:ss}] [{Level}] {Message}";
}

public sealed class AppState : INotifyPropertyChanged
{
    private bool _isBusy;
    private string _connectionText = "等待炉石启动";
    private string _statusText = "正在准备自动连接";

    public ObservableCollection<LogEntry> Logs { get; } = [];

    public bool IsBusy
    {
        get => _isBusy;
        set => SetField(ref _isBusy, value);
    }

    public string ConnectionText
    {
        get => _connectionText;
        set => SetField(ref _connectionText, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
