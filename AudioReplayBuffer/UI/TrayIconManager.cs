using System.Drawing;
using System.IO;
using AudioReplayBuffer.Core;
using WF = System.Windows.Forms;

namespace AudioReplayBuffer.UI;

/// <summary>
/// System tray icon and menu. The window can be closed at any time; this
/// is the surface that always stays alive.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly AppController _controller;
    private readonly WF.NotifyIcon _icon;
    private readonly WF.ToolStripMenuItem _pauseItem;
    private readonly WF.ToolStripMenuItem _autoStartItem;
    private readonly WF.ToolStripMenuItem _clipItem;

    public TrayIconManager(AppController controller, Action openWindow, Action exitApp)
    {
        _controller = controller;

        var openItem = new WF.ToolStripMenuItem("Open Audio Replay Buffer", null, (_, _) => openWindow())
        {
            Font = new Font(WF.Control.DefaultFont, FontStyle.Bold)
        };
        var saveItem = new WF.ToolStripMenuItem("Save replay now", null, (_, _) => _controller.SaveReplay());
        _clipItem = new WF.ToolStripMenuItem("Save last 30 s", null, (_, _) => _controller.SaveClip());
        var clearItem = new WF.ToolStripMenuItem("Clear buffer", null, (_, _) => _controller.ClearBuffer());
        _pauseItem = new WF.ToolStripMenuItem("Pause capture", null, (_, _) => TogglePause());
        _autoStartItem = new WF.ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleAutoStart())
        {
            Checked = StartupRegistry.IsEnabled()
        };

        var menu = new WF.ContextMenuStrip();
        menu.Items.Add(openItem);
        menu.Items.Add(saveItem);
        menu.Items.Add(_clipItem);
        menu.Items.Add(clearItem);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(new WF.ToolStripMenuItem("Exit", null, (_, _) => exitApp()));

        _icon = new WF.NotifyIcon
        {
            Icon = LoadAppIcon(),
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => openWindow();

        _controller.StateChanged += () => OnUiThread(UpdateState);
        _controller.ReplaySaved += (path, duration) => OnUiThread(() =>
        {
            if (_controller.Settings.ShowNotifications)
                _icon.ShowBalloonTip(3000, "Replay saved",
                    $"{Path.GetFileName(path)}  ({AppController.Fmt(duration)})", WF.ToolTipIcon.Info);
        });
        _controller.SaveFailed += message => OnUiThread(() =>
            _icon.ShowBalloonTip(4000, "Save failed", message, WF.ToolTipIcon.Error));
        _controller.SoundboardError += message => OnUiThread(() =>
            _icon.ShowBalloonTip(4000, "Soundboard", message, WF.ToolTipIcon.Warning));

        UpdateState();
    }

    public void ShowWarning(string title, string message)
        => _icon.ShowBalloonTip(4000, title, message, WF.ToolTipIcon.Warning);

    private static void OnUiThread(Action action)
        => System.Windows.Application.Current?.Dispatcher.BeginInvoke(action);

    private void UpdateState()
    {
        bool running = _controller.IsCapturing;
        _pauseItem.Text = running ? "Pause capture" : "Resume capture";
        _clipItem.Text = $"Save last {_controller.Settings.ClipSeconds} s";
        // NotifyIcon.Text is limited to 63 characters.
        _icon.Text = running
            ? $"Audio Replay Buffer — recording ({_controller.Settings.Hotkey})"
            : "Audio Replay Buffer — paused";
    }

    private void TogglePause()
    {
        try
        {
            if (_controller.IsCapturing)
                _controller.StopCapture();
            else
                _controller.StartCapture();
        }
        catch (Exception ex)
        {
            Logger.Log("Pause/resume from tray failed: " + ex);
            _icon.ShowBalloonTip(4000, "Error", ex.Message, WF.ToolTipIcon.Error);
        }
    }

    private void ToggleAutoStart()
    {
        _autoStartItem.Checked = !_autoStartItem.Checked;
        StartupRegistry.SetEnabled(_autoStartItem.Checked);
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/app.ico"));
            if (resource != null)
                using (resource.Stream)
                    return new Icon(resource.Stream);
        }
        catch (Exception ex)
        {
            Logger.Log("Could not load app.ico for the tray, using fallback: " + ex.Message);
        }
        return CreateTrayIcon();
    }

    /// <summary>Fallback generated icon: red dot on a dark circle (record symbol).</summary>
    private static Icon CreateTrayIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            g.FillEllipse(new SolidBrush(Color.FromArgb(45, 45, 48)), 1, 1, 30, 30);
            g.FillEllipse(new SolidBrush(Color.FromArgb(229, 72, 77)), 9, 9, 14, 14);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
