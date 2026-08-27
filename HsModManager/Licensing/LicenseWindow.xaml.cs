using System.Net.Http;
using System.Windows;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace HsModManager.Licensing;

public partial class LicenseWindow : Window
{
    private static readonly MediaBrush NormalStatusBrush = new SolidColorBrush(MediaColor.FromRgb(96, 117, 138));
    private static readonly MediaBrush ErrorStatusBrush = new SolidColorBrush(MediaColor.FromRgb(180, 35, 24));

    private readonly SavedLicenseCredential? _savedCredential;
    private readonly HttpClient _httpClient = new();
    private bool _initialValidationStarted;

    public LicenseWindow()
    {
        InitializeComponent();
        _savedCredential = LicenseCredentialStore.Load();
        KeyTextBox.Text = _savedCredential?.LicenseKey ?? "";
        KeyTextBox.Focus();
        KeyTextBox.SelectAll();
    }

    public LicenseSession? LicenseSession { get; private set; }

    private async void LicenseWindow_ContentRendered(object? sender, EventArgs e)
    {
        if (_initialValidationStarted)
        {
            return;
        }

        _initialValidationStarted = true;
        if (_savedCredential != null)
        {
            await ValidateAndEnterAsync(allowCachedSession: true);
        }
    }

    private async void ValidateButton_Click(object sender, RoutedEventArgs e)
    {
        await ValidateAndEnterAsync(allowCachedSession: false);
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private async Task ValidateAndEnterAsync(bool allowCachedSession)
    {
        SetBusy(true);
        try
        {
            string licenseKey = LicenseCredentialStore.NormalizeKey(KeyTextBox.Text);
            KeyTextBox.Text = licenseKey;
            if (allowCachedSession && TryCreateCachedSession(licenseKey, out LicenseSession? cachedSession))
            {
                LicenseSession = cachedSession;
                DialogResult = true;
                return;
            }

            StatusTextBlock.Foreground = NormalStatusBrush;
            StatusTextBlock.Text = "正在验证卡密...";
            using var timeout = new CancellationTokenSource(LicenseClient.ValidationTimeout);
            LicenseSession session = await LicenseClient.ValidateAsync(_httpClient, licenseKey, timeout.Token);
            LicenseSession = session;
            if (!session.Valid)
            {
                StatusTextBlock.Foreground = ErrorStatusBrush;
                StatusTextBlock.Text = string.IsNullOrWhiteSpace(session.Message) ? "卡密不可用。" : session.Message;
                return;
            }

            LicenseCredentialStore.Save(session);
            DialogResult = true;
        }
        finally
        {
            if (IsVisible)
            {
                SetBusy(false);
            }
        }
    }

    private bool TryCreateCachedSession(string licenseKey, out LicenseSession? session)
    {
        session = null;
        SavedLicenseCredential? saved = _savedCredential;
        if (saved == null
            || !string.Equals(saved.LicenseKey, licenseKey, StringComparison.Ordinal)
            || !string.Equals(saved.ServerUrl.TrimEnd('/'), LicenseClient.GetServerUrl(), StringComparison.OrdinalIgnoreCase)
            || saved.CheckedAtUtc is not { } checkedAtUtc
            || saved.ExpiresAtUtc is not { } expiresAtUtc)
        {
            return false;
        }

        DateTime now = DateTime.UtcNow;
        if (expiresAtUtc <= now || now - checkedAtUtc >= LicenseClient.ValidationRefreshInterval)
        {
            return false;
        }

        session = new LicenseSession(
            LicenseClient.GetServerUrl(),
            licenseKey,
            true,
            string.IsNullOrWhiteSpace(saved.Status) ? "active" : saved.Status,
            string.IsNullOrWhiteSpace(saved.Message) ? "卡密缓存有效。" : saved.Message,
            expiresAtUtc,
            saved.ServerTimeUtc ?? checkedAtUtc,
            checkedAtUtc);
        return true;
    }

    private void SetBusy(bool busy)
    {
        KeyTextBox.IsEnabled = !busy;
        ValidateButton.IsEnabled = !busy;
        ExitButton.IsEnabled = !busy;
        ValidateButton.Content = busy ? "验证中..." : "验证并进入";
    }

    protected override void OnClosed(EventArgs e)
    {
        _httpClient.Dispose();
        base.OnClosed(e);
    }
}
