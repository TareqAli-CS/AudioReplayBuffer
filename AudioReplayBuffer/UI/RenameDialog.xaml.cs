using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AudioReplayBuffer.UI;

public partial class RenameDialog : Window
{
    public bool Saved { get; private set; }
    public string LabelResult => LabelBox.Text.Trim();
    public string FileNameResult => NameBox.Text.Trim();
    public int? SlotResult => SlotBox.SelectedIndex <= 0 ? null : SlotBox.SelectedIndex;
    public int VolumeResult => (int)VolumeSlider.Value;

    public RenameDialog(string label, string fileNameNoExt, string extension, int? currentSlot,
                        Func<int, string?> pathOfSlot, int volumePercent = 100)
    {
        InitializeComponent();
        LabelBox.Text = label;
        NameBox.Text = fileNameNoExt;
        ExtText.Text = extension;
        VolumeSlider.Value = volumePercent;
        VolumeText.Text = $"{volumePercent}%";

        SlotBox.Items.Add("No hotkey");
        for (int slot = 1; slot <= Core.SoundboardStore.SlotCount; slot++)
        {
            string text = $"Ctrl+Alt+{slot}";
            string? occupant = pathOfSlot(slot);
            if (occupant != null && slot != currentSlot)
                text += $"   —   {Path.GetFileNameWithoutExtension(occupant)}";
            SlotBox.Items.Add(text);
        }
        SlotBox.SelectedIndex = currentSlot ?? 0;

        LabelBox.Focus();
        LabelBox.SelectAll();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        string name = FileNameResult;
        if (name.Length == 0)
        {
            ShowError("The file name cannot be empty.");
            return;
        }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ShowError("The file name contains characters Windows does not allow (\\ / : * ? \" < > |).");
            return;
        }
        Saved = true;
        Close();
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeText != null)
            VolumeText.Text = $"{(int)VolumeSlider.Value}%";
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
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
