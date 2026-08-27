using System.Configuration;
using System.Data;
using System.Windows;
using HsModManager.Licensing;

namespace HsModManager;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    public LicenseSession? LicenseSession { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (LocalDevelopmentLicense.TryCreateSession(out LicenseSession? localSession))
        {
            OpenMainWindow(localSession!);
            return;
        }

        var licenseWindow = new LicenseWindow();
        bool verified = licenseWindow.ShowDialog() == true
                        && licenseWindow.LicenseSession is { Valid: true };
        if (!verified)
        {
            Shutdown();
            return;
        }

        OpenMainWindow(licenseWindow.LicenseSession!);
    }

    private void OpenMainWindow(LicenseSession licenseSession)
    {
        LicenseSession = licenseSession;
        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        mainWindow.Show();
    }
}

