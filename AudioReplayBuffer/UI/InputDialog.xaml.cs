using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AudioReplayBuffer.UI;

/// <summary>Minimal dark text prompt; Result is null when cancelled.</summary>
public partial class InputDialog : Window
{
    public string? Result { get; private set; }

    public InputDialog(string title, string prompt, string initial = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = initial;
        Loaded += (_, _) => { InputBox.Focus(); InputBox.SelectAll(); };
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        string value = InputBox.Text.Trim();
        if (value.Length == 0)
        {
            ShowError("Please enter a value.");
            return;
        }
        if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ShowError("This name contains characters Windows does not allow (\\ / : * ? \" < > |).");
            return;
        }
        Result = value;
        Close();
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
