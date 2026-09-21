using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Dahu.Core;

namespace Dahu;

public partial class App : Application
{
    private static Mutex? _mutex;

    [STAThread]
    public static void Main(string[] args)
    {
        var cfg = ConfigService.Load(args);
        Services.AltAccount.ParseArgs(args);

        ThreadPool.GetMinThreads(out var minWorker, out var minIo);
        ThreadPool.SetMinThreads(Math.Max(minWorker, 64), Math.Max(minIo, 64));

        Loc.I.Language = cfg.Language;
        var culture = Loc.Culture(Loc.I.Language);
        Thread.CurrentThread.CurrentUICulture = culture;

        Log.Info($"=== Démarrage Dahu {typeof(App).Assembly.GetName().Version?.ToString(2)} - {Environment.UserDomainName}\\{Environment.UserName} sur {Environment.MachineName} - portable={AppPaths.IsPortable}"
                 + (Services.AltAccount.ActiveUser != null ? $" - compte réseau {Services.AltAccount.ActiveUser}" : ""));

        var app = new App();
        app.InitializeComponent();
        app.DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Exception non gérée (domaine)", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("Exception de tâche non observée", e.Exception); e.SetObserved(); };

        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(System.Windows.Markup.XmlLanguage.GetLanguage(culture.IetfLanguageTag)));

        if (Services.AltAccount.ShouldRelaunch(cfg) && RelaunchAsAltAccount(cfg)) return;

        if (!AcquireInstanceMutex())
        {
            MessageBox.Show(Loc.T("App.AlreadyRunning"), "Dahu", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        app.MainWindow = new Views.MainWindow();
        app.MainWindow.Show();
        app.Run();
        Log.Info("=== Arrêt Dahu");
    }

    private static bool RelaunchAsAltAccount(AppConfig cfg)
    {
        var user = cfg.AltAccountUser.Trim();
        var pwd = Services.AltAccount.Unprotect(cfg.AltAccountPassword);
        while (true)
        {
            pwd ??= Views.Dialogs.AskPassword(null, user);
            if (pwd == null) { Log.Info("Relance sous " + user + " annulée : compte courant conservé"); return false; }
            try { Services.AltAccount.Launch(user, pwd, cfg.AltAccountNetOnly); return true; }
            catch (Exception ex)
            {
                Log.Error("Relance sous " + user, ex);
                MessageBox.Show(string.Format(Loc.T("Alt.Failed"), user, ex.Message), "Dahu", MessageBoxButton.OK, MessageBoxImage.Error);
                pwd = null;
            }
        }
    }

    public static bool AcquireInstanceMutex()
    {
        var mutexName = "Dahu-" + ConfigService.FilePath.ToLowerInvariant().GetHashCode().ToString("X");
        _mutex = new Mutex(true, mutexName, out var createdNew);
        if (createdNew) return true;
        _mutex.Dispose(); _mutex = null;
        return false;
    }

    public static void ReleaseInstanceMutex()
    {
        try { _mutex?.ReleaseMutex(); _mutex?.Dispose(); } catch { }
        _mutex = null;
    }

    private static void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Exception non gérée (UI)", e.Exception);
        try
        {
            MessageBox.Show(string.Format(Loc.T("App.UnexpectedError"), e.Exception.Message, Log.Path),
                "Dahu", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { }
        e.Handled = true;
    }
}
