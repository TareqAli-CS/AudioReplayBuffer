using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ReplayPad.UI;

/// <summary>
/// Shown when a save was rescued because the output folder is blocked
/// (Controlled Folder Access). Offers the two real fixes as buttons.
/// </summary>
public partial class BlockedSaveDialog : Window
{
    /// <summary>Set when the user chose to switch the library to this folder.</summary>
    public string? SwitchToFolder { get; private set; }

    private readonly string _suggestedFolder;

    public BlockedSaveDialog(string blockedFolder)
    {
        InitializeComponent();
        _suggestedFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ReplayPad");
        ExplainText.Text =
            $"Windows Ransomware Protection blocked ReplayPad from saving into \"{blockedFolder}\", " +
            "so the clip was saved to a safe fallback folder instead. " +
            "Pick how you want to fix this permanently:";
        SwitchSub.Text =
            $"Saves will go to \"{_suggestedFolder}\" — a folder Windows doesn't guard. " +
            "Files already in the old folder stay there; move them in Explorer if you want them back in the app.";
    }

    private void OnAllowClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("windowsdefender://ransomwareprotection")
            {
                UseShellExecute = true
            });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("windowsdefender:") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Core.Logger.Log("Could not open Windows Security: " + ex.Message);
            }
        }
        Close();
    }

    private void OnSwitchClick(object sender, RoutedEventArgs e)
    {
        SwitchToFolder = _suggestedFolder;
        Close();
    }

    private void OnDismissClick(object sender, RoutedEventArgs e) => Close();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        int dark = 1;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
    }
}
