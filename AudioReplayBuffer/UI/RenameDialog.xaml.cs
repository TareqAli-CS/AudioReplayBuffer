using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AudioReplayBuffer.UI;

public partial class RenameDialog : Window
{
    private const string NoCategoryItem = "(No category)";
    private const string NewCategoryItem = "➕  New category…";
    private int _lastCategoryIndex;

    public bool Saved { get; private set; }
    public string LabelResult => LabelBox.Text.Trim();
    public string FileNameResult => NameBox.Text.Trim();
    public int? SlotResult => SlotBox.SelectedIndex <= 0 ? null : SlotBox.SelectedIndex;
    public int VolumeResult => (int)VolumeSlider.Value;

    public string? CategoryResult =>
        CategoryBox.SelectedIndex <= 0 || CategoryBox.SelectedItem as string == NewCategoryItem
            ? null
            : CategoryBox.SelectedItem as string;

    public RenameDialog(string label, string fileNameNoExt, string extension, int? currentSlot,
                        Func<int, string?> pathOfSlot, int volumePercent = 100,
                        IReadOnlyList<string>? categories = null, string? currentCategory = null)
    {
        InitializeComponent();
        LabelBox.Text = label;
        NameBox.Text = fileNameNoExt;
        ExtText.Text = extension;
        VolumeSlider.Value = volumePercent;
        VolumeText.Text = $"{volumePercent}%";

        if (categories == null)
        {
            // Not a soundboard-library sound — categories don't apply.
            CategoryLabel.Visibility = Visibility.Collapsed;
            CategoryBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            CategoryBox.Items.Add(NoCategoryItem);
            foreach (string category in categories)
                CategoryBox.Items.Add(category);
            CategoryBox.Items.Add(NewCategoryItem);
            CategoryBox.SelectedIndex = currentCategory != null && categories.Contains(currentCategory)
                ? categories.ToList().IndexOf(currentCategory) + 1
                : 0;
            _lastCategoryIndex = CategoryBox.SelectedIndex;
        }

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

    private void OnCategoryChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CategoryBox.SelectedItem as string != NewCategoryItem)
        {
            _lastCategoryIndex = CategoryBox.SelectedIndex;
            return;
        }

        var prompt = new InputDialog("New category", "Category name (e.g. memes, music)") { Owner = this };
        prompt.ShowDialog();
        if (prompt.Result is string name && name.Length > 0)
        {
            int insertAt = CategoryBox.Items.Count - 1;
            if (!CategoryBox.Items.Contains(name))
                CategoryBox.Items.Insert(insertAt, name);
            CategoryBox.SelectedItem = name;
        }
        else
        {
            CategoryBox.SelectedIndex = _lastCategoryIndex;
        }
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
