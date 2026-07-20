using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ReplayPad.Configuration;
using ReplayPad.Core;
using CaptureMode = ReplayPad.Configuration.CaptureMode;

namespace ReplayPad.UI;

public partial class SettingsWindow : Window
{
    private const string AllAppsItem = "All apps (entire desktop)";
    private const string NoVoiceDeviceItem = "Not set — choose your virtual cable / Voicemod device";
    private static readonly int[] Bitrates = [128, 160, 192, 256, 320];

    private readonly AppController _controller;

    /// <summary>True when the user pressed Save and the settings were applied.</summary>
    public bool Saved { get; private set; }

    public SettingsWindow(AppController controller)
    {
        _controller = controller;
        InitializeComponent();
        LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        var s = _controller.Settings;

        ModeBox.SelectedIndex = s.Mode switch
        {
            CaptureMode.Desktop => 0,
            CaptureMode.Microphone => 1,
            _ => 2
        };

        AppBox.Items.Clear();
        AppBox.Items.Add(AllAppsItem);
        if (!string.IsNullOrWhiteSpace(s.TargetApp))
        {
            AppBox.Items.Add(s.TargetApp);
            AppBox.SelectedIndex = 1;
        }
        else
        {
            AppBox.SelectedIndex = 0;
        }
        ExcludeAppCheck.IsChecked = s.TargetAppExclude;

        DesktopGainSlider.Value = Math.Round(s.DesktopGain * 100);
        MicGainSlider.Value = Math.Round(s.MicrophoneGain * 100);
        MinutesSlider.Value = s.BufferMinutes;
        BitrateBox.SelectedIndex = Math.Max(0, Array.IndexOf(Bitrates, s.Bitrate));
        FolderBox.Text = s.ResolveOutputFolder();
        HotkeyBox.Hotkey = s.Hotkey;
        ClipHotkeyBox.Hotkey = s.ClipHotkey;
        ClipSecondsSlider.Value = s.ClipSeconds;

        VoiceDeviceBox.Items.Clear();
        VoiceDeviceBox.Items.Add(NoVoiceDeviceItem);
        if (!string.IsNullOrWhiteSpace(s.VoiceDevice))
        {
            VoiceDeviceBox.Items.Add(s.VoiceDevice);
            VoiceDeviceBox.SelectedIndex = 1;
        }
        else
        {
            VoiceDeviceBox.SelectedIndex = 0;
        }
        VoiceSpeakersCheck.IsChecked = s.VoiceAlsoSpeakers;
        OverlapCheck.IsChecked = s.SoundboardOverlap;
        LauncherHotkeyBox.Hotkey = s.LauncherHotkey;
        StopHotkeyBox.Hotkey = s.StopHotkey;

        GroqKeyBox.Text = s.GroqApiKey;
        NotifyCheck.IsChecked = s.ShowNotifications;
        AutostartCheck.IsChecked = StartupRegistry.IsEnabled();
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AppBox == null)
            return;
        bool desktopInvolved = ModeBox.SelectedIndex != 1;
        AppBox.IsEnabled = desktopInvolved;
        ExcludeAppCheck.IsEnabled = desktopInvolved;
        DesktopGainSlider.IsEnabled = desktopInvolved;
        MicGainSlider.IsEnabled = ModeBox.SelectedIndex != 0;
    }

    private void OnMinutesChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MinutesText != null)
            MinutesText.Text = $"{(int)MinutesSlider.Value} min";
    }

    private void OnClipSecondsChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ClipSecondsText != null)
            ClipSecondsText.Text = $"{(int)ClipSecondsSlider.Value} s";
    }

    private void OnGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DesktopGainText != null)
            DesktopGainText.Text = $"{(int)DesktopGainSlider.Value}%";
        if (MicGainText != null)
            MicGainText.Text = $"{(int)MicGainSlider.Value}%";
    }

    /// <summary>Fills the app picker with processes that currently have an audio session.</summary>
    private void OnAppBoxOpened(object? sender, EventArgs e)
    {
        string? current = AppBox.SelectedIndex > 0 ? AppBox.SelectedItem as string : null;

        var names = new List<string>();
        try
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(
                NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                uint pid = sessions[i].GetProcessID;
                if (pid == 0)
                    continue;
                try
                {
                    string name = Process.GetProcessById((int)pid).ProcessName;
                    if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                        names.Add(name);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Logger.Log("Could not enumerate audio sessions: " + ex.Message);
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        if (current != null && !names.Contains(current, StringComparer.OrdinalIgnoreCase))
            names.Insert(0, current);

        AppBox.Items.Clear();
        AppBox.Items.Add(AllAppsItem);
        foreach (string name in names)
            AppBox.Items.Add(name);
        AppBox.SelectedItem = current ?? AllAppsItem;
    }

    private void OnVoiceDeviceBoxOpened(object? sender, EventArgs e)
    {
        string? current = VoiceDeviceBox.SelectedIndex > 0 ? VoiceDeviceBox.SelectedItem as string : null;

        List<string> devices;
        try
        {
            devices = VoicePlayer.ListRenderDevices();
        }
        catch (Exception ex)
        {
            Logger.Log("Could not enumerate render devices: " + ex.Message);
            return;
        }

        if (current != null && !devices.Contains(current))
            devices.Insert(0, current);

        VoiceDeviceBox.Items.Clear();
        VoiceDeviceBox.Items.Add(NoVoiceDeviceItem);
        foreach (string name in devices)
            VoiceDeviceBox.Items.Add(name);
        VoiceDeviceBox.SelectedItem = current ?? NoVoiceDeviceItem;
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose where replays are saved" };
        if (Directory.Exists(FolderBox.Text))
            dialog.InitialDirectory = FolderBox.Text;
        if (dialog.ShowDialog(this) == true)
            FolderBox.Text = dialog.FolderName;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var current = _controller.Settings;
        var updated = new AppSettings
        {
            CaptureMode = ModeBox.SelectedIndex switch { 1 => "Microphone", 2 => "Both", _ => "Desktop" },
            TargetApp = AppBox.SelectedIndex > 0 ? (AppBox.SelectedItem as string ?? "") : "",
            TargetAppExclude = ExcludeAppCheck.IsChecked == true,
            DesktopGain = (float)(DesktopGainSlider.Value / 100.0),
            MicrophoneGain = (float)(MicGainSlider.Value / 100.0),
            BufferMinutes = (int)MinutesSlider.Value,
            Bitrate = Bitrates[Math.Max(0, BitrateBox.SelectedIndex)],
            OutputFolder = FolderBox.Text.Trim(),
            Hotkey = HotkeyBox.Hotkey.Trim(),
            ClipHotkey = ClipHotkeyBox.Hotkey.Trim(),
            ClipSeconds = (int)ClipSecondsSlider.Value,
            VoiceDevice = VoiceDeviceBox.SelectedIndex > 0 ? (VoiceDeviceBox.SelectedItem as string ?? "") : "",
            VoiceAlsoSpeakers = VoiceSpeakersCheck.IsChecked == true,
            SoundboardOverlap = OverlapCheck.IsChecked == true,
            LauncherHotkey = LauncherHotkeyBox.Hotkey.Trim(),
            StopHotkey = StopHotkeyBox.Hotkey.Trim(),
            GroqApiKey = GroqKeyBox.Text.Trim(),
            VoiceVolume = current.VoiceVolume,
            ShowNotifications = NotifyCheck.IsChecked == true,
            FileNamePrefix = current.FileNamePrefix,
            KeepAliveSilence = current.KeepAliveSilence,
            FFmpegPath = current.FFmpegPath
        };

        try
        {
            string warning = _controller.ApplySettings(updated);
            StartupRegistry.SetEnabled(AutostartCheck.IsChecked == true);
            Saved = true;

            if (warning.Length > 0)
            {
                StatusText.Text = "Saved, but: " + warning;
                StatusText.Foreground = (Brush)FindResource("WarnBrush");
                return; // stay open so the user can fix the hotkey
            }
            Close();
        }
        catch (Exception ex)
        {
            Logger.Log("Apply settings failed: " + ex);
            StatusText.Text = ex.Message;
            StatusText.Foreground = (Brush)FindResource("WarnBrush");
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnLinkClick(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        int dark = 1;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
    }
}
