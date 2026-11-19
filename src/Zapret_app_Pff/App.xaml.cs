using System.Runtime.InteropServices;
using System.Windows;

namespace ZapretAppPff;

public partial class App : Application
{
    private const string InstanceName = @"Global\Zapret_app_Pff";
    private const string ExitEventName = @"Global\Zapret_app_Pff.Exit";

    private Mutex? _singleInstance;
    private EventWaitHandle? _exitRequest;
    private RegisteredWaitHandle? _exitRegistration;
    private EventWaitHandle? _show;
    private RegisteredWaitHandle? _showRegistration;
    private bool _ownsMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var preparingRemoval = e.Args.Length == 1 && e.Args[0] == "--prepare-uninstall";
        try
        {
            if (RuntimeInformation.OSArchitecture != Architecture.X64)
                throw new PlatformNotSupportedException("Требуется Windows 10 или 11 x64.");
            if (Directory.Exists(WindowsTools.Home) &&
                File.GetAttributes(WindowsTools.Home).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Папка установки является ссылкой.");

            if (preparingRemoval)
            {
                await PrepareUninstallAsync();
                Shutdown();
                return;
            }

            var trayOnly = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
            _singleInstance = new Mutex(true, InstanceName, out _ownsMutex);
            if (!_ownsMutex)
            {
                if (!trayOnly && EventWaitHandle.TryOpenExisting(@"Local\Zapret_app_Pff.Show", out var existing))
                {
                    using (existing)
                        existing.Set();
                }
                Shutdown();
                return;
            }

            Directory.CreateDirectory(WindowsTools.Home);
            var window = new MainWindow(trayOnly);
            MainWindow = window;
            _show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Zapret_app_Pff.Show");
            _showRegistration = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) =>
                Dispatcher.InvokeAsync(window.ShowFromTray), null, Timeout.Infinite, executeOnlyOnce: false);

            _exitRequest = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
            _exitRegistration = ThreadPool.RegisterWaitForSingleObject(_exitRequest, (_, _) =>
                Dispatcher.InvokeAsync(() => { _ = window.ExitAsync(); }), null, Timeout.Infinite, executeOnlyOnce: true);

            if (!trayOnly)
                window.Show();
            _ = window.InitializeAsync();
        }
        catch (Exception ex)
        {
            if (preparingRemoval)
                Console.Error.WriteLine(ex.Message);
            else
                MessageBox.Show(ex.Message, WindowsTools.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static async Task PrepareUninstallAsync()
    {
        using var instance = new Mutex(false, InstanceName);
        if (!WaitForInstance(TimeSpan.Zero))
        {
            if (!EventWaitHandle.TryOpenExisting(ExitEventName, out var request))
                throw new InvalidOperationException("Выберите «Выход» в меню значка Zapret_app_Pff в трее и повторите удаление.");

            using (request)
                request.Set();

            if (!WaitForInstance(TimeSpan.FromSeconds(18)))
                throw new InvalidOperationException("Zapret_app_Pff ещё запущен. Выйдите из него через трей и повторите удаление.");
        }

        try
        {
            await new WindowsTools().RemoveInstallationChangesAsync();
        }
        finally
        {
            instance.ReleaseMutex();
        }

        bool WaitForInstance(TimeSpan timeout)
        {
            try { return instance.WaitOne(timeout); }
            catch (AbandonedMutexException) { return true; }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        (MainWindow as MainWindow)?.Dispose();
        _exitRegistration?.Unregister(null);
        _exitRequest?.Dispose();
        _showRegistration?.Unregister(null);
        _show?.Dispose();
        if (_ownsMutex)
            _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
