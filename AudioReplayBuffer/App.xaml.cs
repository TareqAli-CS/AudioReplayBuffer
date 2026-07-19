using System.Windows;
using AudioReplayBuffer.Configuration;
using AudioReplayBuffer.Core;
using AudioReplayBuffer.UI;
using NAudio.MediaFoundation;

namespace AudioReplayBuffer;

public partial class App : Application
{
    private Mutex? _mutex;
    private AppController? _controller;
    private TrayIconManager? _tray;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppPaths.EnsureAndMigrate();
        InstallCrashHandlers();

        _mutex = new Mutex(initiallyOwned: true, "AudioReplayBuffer_SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "Audio Replay Buffer is already running — look for its icon in the system tray.",
                "Audio Replay Buffer", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        AppSettings settings;
        try
        {
            settings = AppSettings.Load();
        }
        catch (Exception ex)
        {
            Logger.Log("Configuration error: " + ex);
            MessageBox.Show(
                "Could not load appsettings.json:\n\n" + ex.Message,
                "Audio Replay Buffer", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        MediaFoundationApi.Startup();
        _controller = new AppController(settings);

        try
        {
            _controller.StartCapture();
        }
        catch (Exception ex)
        {
            Logger.Log("Failed to start capture: " + ex);
            MessageBox.Show(
                "Audio capture could not be started:\n\n" + ex.Message +
                "\n\nThe app will stay open — check your audio devices and press Resume.",
                "Audio Replay Buffer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _window = new MainWindow(_controller);
        _tray = new TrayIconManager(_controller, ShowMainWindow, ExitApplication);

        string hotkeyError = _controller.RegisterHotkey();
        if (hotkeyError.Length > 0)
        {
            Logger.Log(hotkeyError);
            _tray.ShowWarning("Hotkey problem", hotkeyError + " You can still save from the window or tray.");
        }

        // "--edit <file>" opens the editor directly (capture still runs).
        int editIndex = Array.IndexOf(e.Args, "--edit");
        if (editIndex >= 0 && editIndex + 1 < e.Args.Length && File.Exists(e.Args[editIndex + 1]))
        {
            new EditorWindow(e.Args[editIndex + 1], settings).Show();
            return;
        }

        if (!e.Args.Contains("--minimized"))
            ShowMainWindow();
    }

    /// <summary>
    /// A background recorder must not vanish because one operation threw.
    /// UI-thread exceptions are logged and swallowed so capture keeps
    /// running; non-recoverable ones are at least logged before the
    /// process dies.
    /// </summary>
    private void InstallCrashHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Log("Unhandled UI exception (app kept running): " + args.Exception);
            _tray?.ShowWarning("Unexpected error",
                "Something went wrong but the app is still recording. Details are in log.txt.");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Logger.Log("Fatal unhandled exception: " + args.ExceptionObject);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Log("Unobserved task exception (ignored): " + args.Exception);
            args.SetObserved();
        };
    }

    private void ShowMainWindow()
    {
        if (_window == null)
            return;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ExitApplication()
    {
        _tray?.Dispose();
        _tray = null;
        _controller?.Dispose();
        _controller = null;
        MediaFoundationApi.Shutdown();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _controller?.Dispose();
        base.OnExit(e);
    }
}
