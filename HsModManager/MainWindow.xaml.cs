using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using HsModManager.Controls;
using HsModManager.Models;
using HsModManager.Services;
using WpfButton = System.Windows.Controls.Button;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfMessageBox = System.Windows.MessageBox;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfSlider = System.Windows.Controls.Slider;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfTextBlock = System.Windows.Controls.TextBlock;

namespace HsModManager;

public partial class MainWindow : Window
{
    private readonly AppState _state = new();
    private readonly HsBridgeClient _bridge = new();
    private readonly ModInstaller _installer = new();
    private readonly SkinCatalogCacheService _skinCache = new();
    private readonly ObservableCollection<ConfigItem> _configItems = [];
    private readonly ObservableCollection<ClassSkinSelection> _classSkinSelections = [];

    private SkinCatalogResponse? _skinCatalog;
    private WpfSlider? _timeGearSlider;
    private WpfTextBlock? _timeGearValueText;
    private ConfigItem? _timeGearItem;
    private CancellationTokenSource? _timeGearSaveCts;
    private readonly CancellationTokenSource _windowCts = new();
    private readonly DispatcherTimer _autoConnectTimer;
    private bool _isConnected;
    private bool _isConnecting;
    private bool _isAutoConnectTickRunning;
    private int _connectedPid = -1;
    private bool _initialSkinRefreshPending;
    private bool _skinDatabaseWaitingLogged;
    private string _lastAutoConnectError = "";

    private static readonly float[] TimeGearValues =
    [
        -32f, -16f, -8f, -4f, -3f, -2f,
        1f,
        2f, 3f, 4f, 8f, 16f, 32f
    ];

    private static readonly (string Code, string Name)[] HeroClasses =
    [
        ("MAGE", "法师"),
        ("WARRIOR", "战士"),
        ("PALADIN", "圣骑士"),
        ("HUNTER", "猎人"),
        ("ROGUE", "潜行者"),
        ("PRIEST", "牧师"),
        ("SHAMAN", "萨满祭司"),
        ("WARLOCK", "术士"),
        ("DRUID", "德鲁伊"),
        ("DEMONHUNTER", "恶魔猎手"),
        ("DEATHKNIGHT", "死亡骑士")
    ];

    private readonly HashSet<string> _restartRequiredKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "isPluginEnable",
        "pluginLanague",
        "webServerPort",
        "isInternalModeEnable",
        "fakeDevicePreset",
        "fakeDeviceOs",
        "fakeDeviceScreen",
        "fakeDeviceName"
    };

    private readonly HashSet<string> _disconnectSuggestedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "skinCoin",
        "skinBoard",
        "skinBgsBoard",
        "skinBgsFinisher",
        "skinBob",
        "skinHero",
        "skinOpposingHero",
        "skinPet",
        "skinOpposingPet"
    };

    private readonly HashSet<string> _hiddenConfigKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "webServerPort",
        "isEulaRead",
        "skinCoin",
        "skinCardBack",
        "skinBoard",
        "skinBgsBoard",
        "skinBgsFinisher",
        "skinBob",
        "skinHero",
        "skinOpposingHero",
        "skinPet",
        "skinOpposingPet",
        "isSkinDefalutHeroEnable"
    };

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _state;
        ClassSkinItemsControl.ItemsSource = _classSkinSelections;
        _autoConnectTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _autoConnectTimer.Tick += AutoConnectTimer_Tick;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        AddLog("管理器启动。");
        await LoadSkinCacheAsync();
        await DetectInstallationsAsync();
        _autoConnectTimer.Start();
        await TryAutoConnectAsync();
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        await RunButtonAsync(ConnectButton, "正在连接炉石客户端...", ct => ConnectClientAsync(ct, false));
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _autoConnectTimer.Stop();
        _windowCts.Cancel();
        _timeGearSaveCts?.Cancel();
        _bridge.Dispose();
        _windowCts.Dispose();
    }

    private async void AutoConnectTimer_Tick(object? sender, EventArgs e)
    {
        if (_state.IsBusy || _isConnecting || _isAutoConnectTickRunning || _windowCts.IsCancellationRequested)
        {
            return;
        }

        _isAutoConnectTickRunning = true;
        _isConnecting = true;
        try
        {
            StatusResponse? status = await _bridge.GetStatusAsync(_windowCts.Token);
            if (status == null)
            {
                throw new InvalidOperationException("客户端没有返回状态。");
            }

            if (!_isConnected || status.Game.Pid != _connectedPid)
            {
                await CompleteConnectionAsync(status, true, _windowCts.Token);
            }
            else if (_initialSkinRefreshPending)
            {
                await TryInitialSkinRefreshAsync(_windowCts.Token);
            }
        }
        catch (OperationCanceledException) when (_windowCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            MarkDisconnected();
            LogAutoConnectError(ex);
        }
        finally
        {
            _isConnecting = false;
            _isAutoConnectTickRunning = false;
        }
    }

    private async Task TryAutoConnectAsync()
    {
        if (_state.IsBusy || _isConnecting || _windowCts.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await ConnectClientAsync(_windowCts.Token, true);
        }
        catch (OperationCanceledException) when (_windowCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _state.ConnectionText = "等待炉石启动";
            _state.StatusText = "等待炉石客户端";
            LogAutoConnectError(ex);
        }
    }

    private async Task ConnectClientAsync(CancellationToken cancellationToken, bool automatic)
    {
        if (_isConnecting)
        {
            return;
        }

        _isConnecting = true;
        try
        {
            StatusResponse? status = await _bridge.GetStatusAsync(cancellationToken);
            if (status == null)
            {
                throw new InvalidOperationException("客户端没有返回状态。");
            }

            await CompleteConnectionAsync(status, automatic, cancellationToken);
        }
        catch
        {
            if (!automatic)
            {
                throw;
            }

            MarkDisconnected(false);
            throw;
        }
        finally
        {
            _isConnecting = false;
        }
    }

    private async Task CompleteConnectionAsync(StatusResponse status, bool automatic, CancellationToken cancellationToken)
    {
        bool isNewConnection = !_isConnected || _connectedPid != status.Game.Pid;
        _isConnected = true;
        _connectedPid = status.Game.Pid;
        _state.ConnectionText = $"已连接 PID {status.Game.Pid} / v{status.Plugin.Version}";
        _state.StatusText = automatic ? "已自动连接炉石客户端" : $"桥接成功：{_bridge.BaseUri}";

        if (!isNewConnection && automatic)
        {
            return;
        }

        AddLog(automatic
            ? $"检测到炉石启动，已自动连接 PID {status.Game.Pid}。"
            : $"已连接 HsMod Web：{_bridge.BaseUri}");

        try
        {
            await LoadConfigAsync(cancellationToken);
            if (HasCachedSkinCatalog())
            {
                await SyncSkinSettingsAsync(cancellationToken);
            }
            else
            {
                await TryInitialSkinRefreshAsync(cancellationToken);
            }

            _lastAutoConnectError = "";
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            AddLog($"已连接炉石，但配置同步失败：{ex.Message}", "WARN");
        }
    }

    private void LogAutoConnectError(Exception exception)
    {
        string message = exception.Message;
        if (string.IsNullOrWhiteSpace(message) || message.Equals(_lastAutoConnectError, StringComparison.Ordinal))
        {
            return;
        }

        _lastAutoConnectError = message;
        AddLog($"自动连接失败：{message}", "WARN");
    }

    private void MarkDisconnected(bool writeLog = true)
    {
        bool wasConnected = _isConnected;
        _isConnected = false;
        _connectedPid = -1;
        _initialSkinRefreshPending = !HasCachedSkinCatalog();
        _state.ConnectionText = "等待炉石启动";
        _state.StatusText = "等待炉石客户端";
        if (writeLog && wasConnected)
        {
            AddLog("炉石客户端已关闭或桥接已断开，管理器会继续自动检测。", "WARN");
        }
    }

    private async void DetectButton_Click(object sender, RoutedEventArgs e)
    {
        await DetectInstallationsAsync();
    }

    private async Task DetectInstallationsAsync()
    {
        await RunButtonAsync(DetectButton, "正在自动寻找炉石安装目录...", async ct =>
        {
            List<InstallCandidate> candidates = await HearthstoneLocator.FindInstallationsAsync(ct);

            if (candidates.Count > 0)
            {
                InstallPathTextBox.Text = candidates[0].Path;
                AddLog($"找到 {candidates.Count} 个可能的炉石目录。");
            }
            else
            {
                AddLog("未自动找到炉石目录，请手动选择。", "WARN");
            }
        });
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择 Hearthstone 客户端根目录",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (!string.IsNullOrWhiteSpace(InstallPathTextBox.Text) && Directory.Exists(InstallPathTextBox.Text))
        {
            dialog.SelectedPath = InstallPathTextBox.Text;
        }

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            InstallPathTextBox.Text = dialog.SelectedPath;
        }
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        await RunButtonAsync(InstallButton, "正在安装 HsMod...", async ct =>
        {
            var progress = new Progress<string>(message => AddLog(message));
            InstallResult result = await _installer.InstallAsync(InstallPathTextBox.Text, progress, ct);

            if (!result.Success)
            {
                AddLog(result.Message, "ERROR");
                WpfMessageBox.Show(result.Message, "安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            AddLog(result.Message);
            WpfMessageBox.Show(result.Message, "安装成功", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    private async void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        MessageBoxResult confirmation = WpfMessageBox.Show(
            "卸载会先关闭炉石客户端，然后删除 HsMod 插件、备份、配置、工作目录和皮肤缓存。\n\n共享的 BepInEx、运行库和其他插件会保留。是否继续？",
            "确认卸载 HsMod",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        await RunButtonAsync(UninstallButton, "正在卸载 HsMod...", async ct =>
        {
            var progress = new Progress<string>(message => AddLog(message));
            InstallResult result = await _installer.UninstallAsync(InstallPathTextBox.Text, progress, ct);
            AddLog(result.Message, result.Success ? "INFO" : "ERROR");

            WpfMessageBox.Show(
                result.Message,
                result.Success ? "卸载完成" : "卸载失败",
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);

            if (result.Success)
            {
                MarkDisconnected(false);
                _skinCatalog = new SkinCatalogResponse();
                _classSkinSelections.Clear();
            }
        });
    }

    private void CopyInstallLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InstallLogTextBox.Text))
        {
            AddLog("当前没有可复制的日志。", "WARN");
            return;
        }

        System.Windows.Clipboard.SetText(InstallLogTextBox.Text);
        _state.StatusText = "安装日志已复制到剪贴板";
    }

    private async void ReloadConfigButton_Click(object sender, RoutedEventArgs e)
    {
        await RunButtonAsync(ReloadConfigButton, "正在读取配置...", LoadConfigAsync);
    }

    private void ConfigSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        BuildConfigControls();
    }

    private void ShowAdvancedCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (ShowAdvancedCheckBox.IsChecked == true)
        {
            WpfMessageBox.Show("高级配置可能影响插件稳定性。请只修改你明确理解的选项。", "高级配置提醒", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        BuildConfigControls();
    }

    private async Task LoadConfigAsync(CancellationToken cancellationToken)
    {
        ConfigMetadataResponse? response = await _bridge.GetConfigAsync("zhCN", cancellationToken);
        if (response == null)
        {
            throw new InvalidOperationException("配置接口没有返回数据。");
        }

        _configItems.Clear();
        foreach (ConfigItem item in response.Groups.Values.SelectMany(x => x))
        {
            _configItems.Add(item);
        }

        BuildConfigControls();
        AddLog($"已读取 {_configItems.Count} 个配置项。");
    }

    private void BuildConfigControls()
    {
        if (ConfigGroupsPanel == null)
        {
            return;
        }

        _timeGearSaveCts?.Cancel();
        _timeGearSlider = null;
        _timeGearValueText = null;
        _timeGearItem = null;
        ConfigGroupsPanel.Children.Clear();
        string keyword = ConfigSearchTextBox?.Text?.Trim() ?? "";
        bool showAdvanced = ShowAdvancedCheckBox?.IsChecked == true;

        IEnumerable<ConfigItem> visibleItems = _configItems
            .Where(item => !_hiddenConfigKeys.Contains(item.Key))
            .Where(item => showAdvanced || !item.IsAdvanced)
            .Where(item => MatchesConfigSearch(item, keyword));

        foreach (IGrouping<string, ConfigItem> group in visibleItems.GroupBy(item => item.Label).OrderBy(g => GroupOrder(g.Key)))
        {
            var wrap = new WrapPanel
            {
                Orientation = WpfOrientation.Horizontal,
                Margin = new Thickness(8, 12, 0, 0)
            };

            List<ConfigItem> orderedItems = group.OrderBy(ItemOrder).ToList();
            ConfigItem? timeGearEnable = orderedItems.FirstOrDefault(item => item.Key.Equals("isTimeGearEnable", StringComparison.OrdinalIgnoreCase));
            ConfigItem? timeGear = orderedItems.FirstOrDefault(item => item.Key.Equals("timeGear", StringComparison.OrdinalIgnoreCase));
            bool hasTimeGearCard = timeGearEnable != null && timeGear != null;
            if (hasTimeGearCard)
            {
                wrap.Children.Add(CreateTimeGearCard(timeGearEnable!, timeGear!));
            }

            foreach (ConfigItem item in orderedItems.Where(item =>
                         !hasTimeGearCard ||
                         (!item.Key.Equals("isTimeGearEnable", StringComparison.OrdinalIgnoreCase) &&
                          !item.Key.Equals("timeGear", StringComparison.OrdinalIgnoreCase))))
            {
                wrap.Children.Add(CreateConfigCard(item));
            }

            (string background, string border, string foreground) = GetGroupPalette(group.Key);
            var header = new Border
            {
                Background = CreateBrush(background),
                BorderBrush = CreateBrush(border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(13, 9, 13, 9),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                Child = new WpfTextBlock
                {
                    Text = $"{group.Key} ({group.Count()})",
                    Foreground = CreateBrush(foreground),
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 14
                }
            };

            var expander = new Expander
            {
                Header = header,
                IsExpanded = false,
                Margin = new Thickness(0, 0, 0, 12),
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
                Content = wrap
            };

            ConfigGroupsPanel.Children.Add(expander);
        }
    }

    private static (string Background, string Border, string Foreground) GetGroupPalette(string group)
    {
        return group switch
        {
            "全局" => ("#DCEBFA", "#A9C7E3", "#24587F"),
            "炉石" => ("#E2F2E8", "#AFD3BC", "#2E6543"),
            "皮肤" => ("#F5E5EE", "#DAB5C9", "#7B3B5B"),
            "酒馆" => ("#FFF0D8", "#E8CAA0", "#805A23"),
            "佣兵" => ("#F1E9DA", "#D6C29E", "#72572B"),
            "开包" => ("#E8E5F5", "#C3BBDD", "#554B7D"),
            "优化" => ("#DFF1F1", "#ADD2D2", "#2E6666"),
            "快捷键" => ("#E5ECF6", "#BBC9DC", "#405E7F"),
            "好友" => ("#F8E8E1", "#E0BEB1", "#7D4D3A"),
            "开发" => ("#E7EBEF", "#BBC5CE", "#4B5D6D"),
            "模拟" => ("#F2E7DE", "#D7BEAA", "#76523B"),
            "HsMod" => ("#E2EAF7", "#B6C7DF", "#3B5E86"),
            _ => ("#E7EDF3", "#C2CED9", "#435D73")
        };
    }

    private static System.Windows.Media.Brush CreateBrush(string color)
    {
        return (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(color)!;
    }

    private Border CreateTimeGearCard(ConfigItem enableItem, ConfigItem speedItem)
    {
        var panel = new StackPanel();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        header.Children.Add(new WpfTextBlock
        {
            Text = "变速齿轮",
            FontWeight = FontWeights.SemiBold,
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center
        });

        var enableCheckBox = new WpfCheckBox
        {
            Content = "启用",
            IsChecked = enableItem.Value.Equals("true", StringComparison.OrdinalIgnoreCase),
            Tag = enableItem,
            VerticalAlignment = VerticalAlignment.Center
        };
        enableCheckBox.Checked += BoolConfigChanged;
        enableCheckBox.Unchecked += BoolConfigChanged;
        Grid.SetColumn(enableCheckBox, 1);
        header.Children.Add(enableCheckBox);
        panel.Children.Add(header);

        panel.Children.Add(new WpfTextBlock
        {
            Text = "变速倍率",
            Foreground = System.Windows.Media.Brushes.SlateGray,
            Margin = new Thickness(0, 14, 0, 4)
        });

        float currentValue = ParseFloat(speedItem.Value, 1f);
        int currentIndex = FindClosestTimeGearIndex(currentValue);
        _timeGearItem = speedItem;
        _timeGearValueText = new WpfTextBlock
        {
            Text = FormatTimeGearValue(TimeGearValues[currentIndex]),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8)
        };
        panel.Children.Add(_timeGearValueText);

        var controls = new Grid();
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });

        WpfButton decreaseButton = CreateTimeGearStepButton("−", -1);
        controls.Children.Add(decreaseButton);

        _timeGearSlider = new WpfSlider
        {
            Minimum = 0,
            Maximum = TimeGearValues.Length - 1,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            IsMoveToPointEnabled = true,
            Value = currentIndex,
            Margin = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = speedItem
        };
        _timeGearSlider.ValueChanged += TimeGearSlider_ValueChanged;
        Grid.SetColumn(_timeGearSlider, 1);
        controls.Children.Add(_timeGearSlider);

        WpfButton increaseButton = CreateTimeGearStepButton("+", 1);
        Grid.SetColumn(increaseButton, 2);
        controls.Children.Add(increaseButton);
        panel.Children.Add(controls);
        panel.Children.Add(CreateEffectTip(speedItem));

        return new Border
        {
            Background = System.Windows.Media.Brushes.White,
            BorderBrush = System.Windows.Media.Brushes.LightSteelBlue,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Width = 732,
            MinHeight = 170,
            Margin = new Thickness(0, 0, 12, 12),
            Child = panel
        };
    }

    private WpfButton CreateTimeGearStepButton(string content, int direction)
    {
        var button = new WpfButton
        {
            Content = content,
            Width = 38,
            Height = 38,
            Padding = new Thickness(0),
            FontSize = 20,
            Tag = direction,
            ToolTip = direction < 0 ? "降低倍率" : "提高倍率"
        };
        button.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
        button.Click += TimeGearStepButton_Click;
        return button;
    }

    private void TimeGearStepButton_Click(object sender, RoutedEventArgs e)
    {
        if (_timeGearSlider == null || sender is not WpfButton button || button.Tag is not int direction)
        {
            return;
        }

        _timeGearSlider.Value = Math.Clamp((int)Math.Round(_timeGearSlider.Value) + direction, 0, TimeGearValues.Length - 1);
    }

    private async void TimeGearSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int index = Math.Clamp((int)Math.Round(e.NewValue), 0, TimeGearValues.Length - 1);
        float value = TimeGearValues[index];
        if (_timeGearValueText != null)
        {
            _timeGearValueText.Text = FormatTimeGearValue(value);
        }

        if (_timeGearItem == null)
        {
            return;
        }

        _timeGearSaveCts?.Cancel();
        var cts = new CancellationTokenSource();
        _timeGearSaveCts = cts;
        try
        {
            await Task.Delay(250, cts.Token);
            await SaveConfigKeyAsync(_timeGearItem.Key, value.ToString("0.##", CultureInfo.InvariantCulture));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static int FindClosestTimeGearIndex(float value)
    {
        int closestIndex = 0;
        float closestDistance = float.MaxValue;
        for (int index = 0; index < TimeGearValues.Length; index++)
        {
            float distance = Math.Abs(TimeGearValues[index] - value);
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closestIndex = index;
            }
        }

        return closestIndex;
    }

    private static float ParseFloat(string value, float fallback)
    {
        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result) ||
            float.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out result))
        {
            return result;
        }

        return fallback;
    }

    private static string FormatTimeGearValue(float value)
    {
        if (value < -1f)
        {
            return $"1/{Math.Abs(value):0} 倍";
        }

        return value == 1f ? "1 倍（正常）" : $"{value:0.##} 倍";
    }

    private static bool MatchesConfigSearch(ConfigItem item, string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return true;
        }

        return item.Key.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               item.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               item.Label.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               item.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private Border CreateConfigCard(ConfigItem item)
    {
        var panel = new StackPanel();

        panel.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontWeight = FontWeights.SemiBold,
            FontSize = 15
        });

        panel.Children.Add(new TextBlock
        {
            Text = item.Key,
            Foreground = System.Windows.Media.Brushes.SlateGray,
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 5)
        });

        panel.Children.Add(new TextBlock
        {
            Text = item.Description,
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.DimGray,
            MaxHeight = 54
        });

        panel.Children.Add(CreateConfigEditor(item));
        panel.Children.Add(CreateEffectTip(item));

        return new Border
        {
            Background = System.Windows.Media.Brushes.White,
            BorderBrush = System.Windows.Media.Brushes.LightSteelBlue,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Width = 360,
            MinHeight = 170,
            Margin = new Thickness(0, 0, 12, 12),
            Child = panel
        };
    }

    private WpfTextBlock CreateEffectTip(ConfigItem item)
    {
        string text = _restartRequiredKeys.Contains(item.Key)
            ? "生效方式：需要重启炉石客户端"
            : _disconnectSuggestedKeys.Contains(item.Key)
                ? "生效方式：保存后热加载，对局中可能需要重新连接"
                : "生效方式：实时生效";

        return new WpfTextBlock
        {
            Text = text,
            Foreground = CreateBrush("#B42318"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
    }

    private FrameworkElement CreateConfigEditor(ConfigItem item)
    {
        if (item.Type == "bool")
        {
            var checkBox = new WpfCheckBox
            {
                Content = "启用",
                IsChecked = item.Value.Equals("true", StringComparison.OrdinalIgnoreCase),
                Margin = new Thickness(0, 12, 0, 0),
                Tag = item
            };
            checkBox.Checked += BoolConfigChanged;
            checkBox.Unchecked += BoolConfigChanged;
            return checkBox;
        }

        if (item.Type == "enum")
        {
            var comboBox = new WpfComboBox
            {
                ItemsSource = item.EnumValues,
                SelectedItem = item.Value,
                Margin = new Thickness(0, 12, 0, 0),
                Tag = item
            };
            comboBox.SelectionChanged += EnumConfigChanged;
            return comboBox;
        }

        var dock = new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(0, 12, 0, 0),
            Tag = item
        };

        var saveButton = new WpfButton
        {
            Content = "保存",
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(12, 6, 12, 6),
            Tag = item
        };
        saveButton.Click += TextConfigSave_Click;
        DockPanel.SetDock(saveButton, Dock.Right);
        dock.Children.Add(saveButton);

        var textBox = new WpfTextBox
        {
            Text = item.Value,
            Tag = item
        };
        textBox.KeyDown += TextConfigTextBox_KeyDown;
        dock.Children.Add(textBox);

        return dock;
    }

    private async void BoolConfigChanged(object sender, RoutedEventArgs e)
    {
        if (sender is WpfCheckBox checkBox && checkBox.Tag is ConfigItem item)
        {
            await SaveConfigKeyAsync(item.Key, checkBox.IsChecked == true ? "true" : "false");
        }
    }

    private async void EnumConfigChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is WpfComboBox comboBox && comboBox.Tag is ConfigItem item && comboBox.SelectedItem is string selected)
        {
            await SaveConfigKeyAsync(item.Key, selected);
        }
    }

    private async void TextConfigSave_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton button || button.Tag is not ConfigItem item)
        {
            return;
        }

        WpfTextBox? textBox = FindSiblingTextBox(button);
        if (textBox != null)
        {
            await SaveConfigKeyAsync(item.Key, textBox.Text);
        }
    }

    private async void TextConfigTextBox_KeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is WpfTextBox textBox && textBox.Tag is ConfigItem item)
        {
            await SaveConfigKeyAsync(item.Key, textBox.Text);
            e.Handled = true;
        }
    }

    private static WpfTextBox? FindSiblingTextBox(WpfButton button)
    {
        if (button.Parent is DockPanel panel)
        {
            return panel.Children.OfType<WpfTextBox>().FirstOrDefault();
        }

        return null;
    }

    private async Task SaveConfigKeyAsync(string key, string value)
    {
        try
        {
            _state.IsBusy = true;
            ApiResult result = await _bridge.SaveConfigAsync(key, value);
            if (!result.IsSuccess)
            {
                AddLog($"保存失败 {key}: {result.Output ?? result.Error}", "ERROR");
                return;
            }

            ConfigItem? item = _configItems.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (item != null)
            {
                item.Value = value;
            }

            AddLog($"已保存 {key} = {value}");
            AddEffectHint(key);
        }
        catch (Exception ex)
        {
            AddLog($"保存配置异常 {key}: {ex.Message}", "ERROR");
        }
        finally
        {
            _state.IsBusy = false;
        }
    }

    private void AddEffectHint(string key)
    {
        if (_restartRequiredKeys.Contains(key))
        {
            AddLog($"{key} 需要重启炉石后完全生效。", "WARN");
        }
        else if (_disconnectSuggestedKeys.Contains(key))
        {
            AddLog($"{key} 已写入；若对局中未立即变化，请热加载皮肤或模拟断线。", "WARN");
        }
        else
        {
            AddLog($"{key} 通常会热更新；如果当前场景没有刷新，请切换界面或重启炉石。");
        }
    }

    private async Task LoadSkinCacheAsync()
    {
        SkinCatalogCache? cache = await _skinCache.LoadAsync(_windowCts.Token);
        _skinCatalog = new SkinCatalogResponse();
        if (cache != null && HasCompleteHeroClassCatalog(cache.Catalog))
        {
            _skinCatalog.Catalog = cache.Catalog;
            _initialSkinRefreshPending = false;
            PopulateSkinControls();
            int heroSkinCount = cache.Catalog.Heroes.Count(item => item.Id > 0);
            AddLog($"已从本地缓存加载 {heroSkinCount} 个英雄皮肤。需要更新时点击“刷新皮肤列表”。");
        }
        else
        {
            _initialSkinRefreshPending = true;
            BuildClassSkinSelectors();
            AddLog("本地尚无皮肤缓存，首次连接炉石后会自动读取。", "WARN");
        }
    }

    private bool HasCachedSkinCatalog()
    {
        return _skinCatalog != null && HasCompleteHeroClassCatalog(_skinCatalog.Catalog);
    }

    private static bool HasCompleteHeroClassCatalog(SkinCatalog catalog)
    {
        HashSet<string> availableClasses = catalog.Heroes
            .Where(item => item.Id > 0)
            .Select(item => NormalizeClassCode(item.HeroClass))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return HeroClasses.All(heroClass => availableClasses.Contains(heroClass.Code));
    }

    private async Task RefreshSkinCatalogAsync(CancellationToken cancellationToken)
    {
        SkinCatalogResponse? response = await _bridge.GetSkinsAsync(cancellationToken);
        if (response == null)
        {
            throw new InvalidOperationException("皮肤接口没有返回数据。");
        }

        if (!HasCompleteHeroClassCatalog(response.Catalog))
        {
            throw new InvalidOperationException("炉石皮肤数据库尚未加载完成。");
        }

        _skinCatalog = response;
        _initialSkinRefreshPending = false;
        _skinDatabaseWaitingLogged = false;
        PopulateSkinControls();
        await _skinCache.SaveAsync(response.Catalog, cancellationToken);

        int heroSkinCount = response.Catalog.Heroes.Count(item => item.Id > 0);
        AddLog($"已从当前炉石客户端读取并缓存 {heroSkinCount} 个英雄皮肤，并按 11 个职业整理。");
    }

    private async Task TryInitialSkinRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshSkinCatalogAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            _initialSkinRefreshPending = true;
            if (!_skinDatabaseWaitingLogged)
            {
                AddLog("已连接炉石，正在等待皮肤数据库加载完成。", "WARN");
                _skinDatabaseWaitingLogged = true;
            }
        }
    }

    private async Task SyncSkinSettingsAsync(CancellationToken cancellationToken)
    {
        if (_skinCatalog == null)
        {
            return;
        }

        SkinSettingsResponse? settings = await _bridge.GetSkinSettingsAsync(cancellationToken);
        if (settings == null)
        {
            throw new InvalidOperationException("客户端没有返回当前皮肤设置。");
        }

        _skinCatalog.Current = settings.Current;
        _skinCatalog.HsSkins = settings.HsSkins;
        PopulateSkinControls();
        AddLog("已使用本地皮肤缓存，并同步当前皮肤设置。");
    }

    private void PopulateSkinControls()
    {
        if (_skinCatalog == null)
        {
            return;
        }

        SetSkinCombo(CoinComboBox, _skinCatalog.Catalog.Coins, "skinCoin");
        SetSkinCombo(CardBackComboBox, _skinCatalog.Catalog.CardBacks, "skinCardBack");
        SetSkinCombo(BoardComboBox, _skinCatalog.Catalog.Boards, "skinBoard");
        SetSkinCombo(BgsBoardComboBox, _skinCatalog.Catalog.BattlegroundBoards, "skinBgsBoard");
        SetSkinCombo(BgsFinisherComboBox, _skinCatalog.Catalog.BattlegroundFinishers, "skinBgsFinisher");
        SetSkinCombo(BobComboBox, _skinCatalog.Catalog.Bobs, "skinBob");
        SetSkinCombo(PetComboBox, _skinCatalog.Catalog.Pets, "skinPet");
        SetSkinCombo(OpposingPetComboBox, _skinCatalog.Catalog.Pets, "skinOpposingPet");
        BuildClassSkinSelectors();
    }

    private async void RefreshSkinListButton_Click(object sender, RoutedEventArgs e)
    {
        await RunButtonAsync(RefreshSkinListButton, "正在刷新并缓存皮肤列表...", async ct =>
        {
            if (!_isConnected)
            {
                throw new InvalidOperationException("当前未连接炉石客户端。启动炉石后管理器会自动连接。");
            }

            await RefreshSkinCatalogAsync(ct);
        });
    }

    private void SetSkinCombo(SearchableSkinComboBox comboBox, List<SkinItem> items, string configKey)
    {
        comboBox.ItemsSource = items;
        comboBox.Tag = configKey;

        if (_skinCatalog?.Current.TryGetValue(configKey, out int current) == true && items.Any(item => item.Id == current))
        {
            comboBox.SelectedId = current;
        }
        else
        {
            comboBox.SelectedId = items.Count > 0 ? items[0].Id : -1;
        }
    }

    private void BuildClassSkinSelectors()
    {
        if (_skinCatalog == null)
        {
            return;
        }

        Dictionary<int, List<int>> existing = ParseHsSkinMappings(_skinCatalog.HsSkins);
        List<SkinItem> heroes = _skinCatalog.Catalog.Heroes
            .Where(item => item.Id > 0)
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .ToList();

        _classSkinSelections.Clear();
        foreach ((string classCode, string className) in HeroClasses)
        {
            List<SkinItem> classHeroes = heroes
                .Where(item => NormalizeClassCode(item.HeroClass) == classCode)
                .OrderBy(item => item.Name, StringComparer.CurrentCulture)
                .ThenBy(item => item.Id)
                .ToList();

            var options = new List<SkinItem>
            {
                new() { Id = -1, Name = "保持游戏选择", Category = "hero", HeroClass = classCode },
                new() { Id = -2, Name = "随机（每场自动切换）", Category = "hero", HeroClass = classCode }
            };
            options.AddRange(classHeroes);

            _classSkinSelections.Add(new ClassSkinSelection
            {
                ClassCode = classCode,
                DisplayName = className,
                Options = options,
                SelectedId = GetCurrentClassSelection(classHeroes, existing)
            });
        }
    }

    private static int GetCurrentClassSelection(IReadOnlyCollection<SkinItem> classHeroes, IReadOnlyDictionary<int, List<int>> mappings)
    {
        var mappedTargets = classHeroes
            .Where(hero => mappings.ContainsKey(hero.Id))
            .Select(hero => mappings[hero.Id])
            .ToList();

        if (mappedTargets.Count == 0)
        {
            return -1;
        }

        if (mappedTargets.Any(targets => targets.Count > 1))
        {
            return -2;
        }

        int[] distinctTargets = mappedTargets.SelectMany(targets => targets).Distinct().ToArray();
        return distinctTargets.Length == 1 && classHeroes.Any(hero => hero.Id == distinctTargets[0])
            ? distinctTargets[0]
            : -2;
    }

    private static Dictionary<int, List<int>> ParseHsSkinMappings(string content)
    {
        var result = new Dictionary<int, List<int>>();
        using var reader = new StringReader(content ?? "");
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            string[] parts = line.Split(':', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            if (!int.TryParse(parts[0].Trim(), out int source))
            {
                continue;
            }

            List<int> targets = parts[1]
                .Split(',')
                .Select(value => int.TryParse(value.Trim(), out int target) ? target : -1)
                .Where(target => target > 0)
                .Distinct()
                .ToList();
            if (targets.Count > 0)
            {
                result[source] = targets;
            }
        }

        return result;
    }

    private async void SaveAndApplySkinsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunButtonAsync(SaveAndApplySkinsButton, "正在保存并应用皮肤...", async ct =>
        {
            if (!_isConnected)
            {
                throw new InvalidOperationException("当前未连接炉石客户端。启动炉石后管理器会自动连接。");
            }

            if (_skinCatalog == null)
            {
                throw new InvalidOperationException("请先连接炉石客户端，管理器会自动读取全部皮肤。");
            }

            SearchableSkinComboBox[] globalSkinCombos =
            [
                CoinComboBox,
                CardBackComboBox,
                BoardComboBox,
                BgsBoardComboBox,
                BgsFinisherComboBox,
                BobComboBox,
                PetComboBox,
                OpposingPetComboBox
            ];

            foreach (SearchableSkinComboBox comboBox in globalSkinCombos)
            {
                if (comboBox.Tag is string key)
                {
                    string value = comboBox.SelectedId.ToString(CultureInfo.InvariantCulture);
                    await SaveRequiredConfigAsync(key, value, ct);
                }
            }

            if (PetComboBox.SelectedId > 0)
            {
                // The upstream local Battlegrounds pet controller is intentionally gated by
                // the local collection feature. Selecting a pet here is an explicit opt-in.
                await SaveRequiredConfigAsync("isBgsUnlockCollectionEnable", "true", ct);
            }

            await SaveRequiredConfigAsync("skinHero", "-1", ct);
            await SaveRequiredConfigAsync("skinOpposingHero", "-1", ct);
            await SaveRequiredConfigAsync("isSkinDefalutHeroEnable", "false", ct);

            string configContent = BuildClassSkinConfig();
            ApiResult update = await _bridge.UpdateHsSkinsAsync(configContent, ct);
            if (!update.IsSuccess)
            {
                throw new InvalidOperationException($"保存职业皮肤失败：{update.Output ?? update.Error}");
            }

            _skinCatalog.HsSkins = configContent;
            ApiResult apply = await _bridge.RunActionAsync("refreshPetCorners", ct);
            if (!apply.IsSuccess)
            {
                AddLog($"皮肤已保存，将在下一局生效；当前场景刷新失败：{apply.Output ?? apply.Error}", "WARN");
            }
            else
            {
                AddLog("皮肤已保存；英雄等对局资源将在下一局生效，宠物配置已按上游实现请求刷新。");
            }

            WpfMessageBox.Show("皮肤设置已保存并应用。", "应用成功", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    private async Task SaveRequiredConfigAsync(string key, string value, CancellationToken cancellationToken)
    {
        ApiResult result = await _bridge.SaveConfigAsync(key, value, cancellationToken);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"保存 {key} 失败：{result.Output ?? result.Error}");
        }
    }

    private string BuildClassSkinConfig()
    {
        var managedSourceIds = _classSkinSelections
            .SelectMany(selection => selection.Options)
            .Where(item => item.Id > 0)
            .Select(item => item.Id)
            .ToHashSet();

        var builder = new StringBuilder();
        builder.AppendLine("# HsMod Manager 职业皮肤配置");
        builder.AppendLine("# 固定皮肤和每场随机均由管理器自动维护。");

        foreach (string line in ReadMappingLines(_skinCatalog?.HsSkins ?? string.Empty))
        {
            string[] parts = line.Split(':', 2);
            if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out int sourceId) && !managedSourceIds.Contains(sourceId))
            {
                builder.AppendLine(line);
            }
        }

        foreach (ClassSkinSelection selection in _classSkinSelections)
        {
            int[] sourceIds = selection.Options.Where(item => item.Id > 0).Select(item => item.Id).Distinct().ToArray();
            if (selection.SelectedId == -1 || sourceIds.Length == 0)
            {
                continue;
            }

            string targets = selection.SelectedId == -2
                ? string.Join(",", sourceIds)
                : selection.SelectedId.ToString(CultureInfo.InvariantCulture);

            foreach (int sourceId in sourceIds)
            {
                builder.AppendLine($"{sourceId}:{targets}");
            }
        }

        return builder.ToString();
    }

    private static IEnumerable<string> ReadMappingLines(string content)
    {
        using var reader = new StringReader(content ?? string.Empty);
        while (reader.ReadLine() is { } line)
        {
            string current = line.Trim();
            if (current.Length > 0 && !current.StartsWith("#", StringComparison.Ordinal))
            {
                yield return current;
            }
        }
    }

    private static string NormalizeClassCode(string value)
    {
        return (value ?? string.Empty)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
    }

    private async Task RunButtonAsync(WpfButton button, string status, Func<CancellationToken, Task> action)
    {
        if (_state.IsBusy)
        {
            return;
        }

        try
        {
            button.IsEnabled = false;
            _state.IsBusy = true;
            _state.StatusText = status;
            await action(CancellationToken.None);
        }
        catch (Exception ex)
        {
            AddLog(ex.Message, "ERROR");
            _state.StatusText = "操作失败：" + ex.Message;
        }
        finally
        {
            _state.IsBusy = false;
            button.IsEnabled = true;
            if (!_state.StatusText.StartsWith("操作失败", StringComparison.Ordinal))
            {
                _state.StatusText = "就绪";
            }
        }
    }

    private void AddLog(string message, string level = "INFO")
    {
        Dispatcher.Invoke(() =>
        {
            _state.Logs.Insert(0, new LogEntry
            {
                Level = level,
                Message = message
            });

            while (_state.Logs.Count > 300)
            {
                _state.Logs.RemoveAt(_state.Logs.Count - 1);
            }

            if (InstallLogTextBox != null)
            {
                InstallLogTextBox.Text = string.Join(Environment.NewLine, _state.Logs.Select(entry => entry.Display));
                InstallLogTextBox.ScrollToHome();
            }
        });
    }

    private static int GroupOrder(string group)
    {
        return group switch
        {
            "全局" => 0,
            "炉石" => 1,
            "皮肤" => 2,
            "酒馆" => 3,
            "佣兵" => 4,
            "开包" => 5,
            "优化" => 6,
            "快捷键" => 7,
            "好友" => 8,
            "开发" => 9,
            "模拟" => 10,
            _ => 99
        };
    }

    private static int ItemOrder(ConfigItem item)
    {
        return item.Key switch
        {
            "isPluginEnable" => 0,
            "isTimeGearEnable" => 1,
            "timeGear" => 2,
            "targetFrameRate" => 3,
            "goldenCardState" => 4,
            "maxCardState" => 5,
            "mercenaryDiamondCardState" => 6,
            "skinHero" => 7,
            "skinBob" => 8,
            "skinBoard" => 9,
            _ => 100
        };
    }
}
