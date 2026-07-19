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

    private static readonly string?[] PadColors =
        [null, "#E5484D", "#E5A33B", "#46C066", "#4F8CFF", "#A06CE5", "#E56CB3", "#8B8B96"];
    private string? _selectedColor;
    private readonly List<System.Windows.Controls.Border> _swatches = [];

    public string? ColorResult => _selectedColor;

    public string? CategoryResult =>
        CategoryBox.SelectedIndex <= 0 || CategoryBox.SelectedItem as string == NewCategoryItem
            ? null
            : CategoryBox.SelectedItem as string;

    public RenameDialog(string label, string fileNameNoExt, string extension, int? currentSlot,
                        Func<int, string?> pathOfSlot, int volumePercent = 100,
                        IReadOnlyList<string>? categories = null, string? currentCategory = null,
                        string? currentColor = null)
    {
        InitializeComponent();
        LabelBox.Text = label;
        NameBox.Text = fileNameNoExt;
        ExtText.Text = extension;
        VolumeSlider.Value = volumePercent;
        VolumeText.Text = $"{volumePercent}%";

        _selectedColor = currentColor;
        BuildColorRow();

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

    private void BuildColorRow()
    {
        ColorRow.Children.Clear();
        _swatches.Clear();
        foreach (string? color in PadColors)
        {
            var swatch = new System.Windows.Controls.Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(7),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                BorderThickness = new Thickness(2),
                Tag = color,
                ToolTip = color ?? "No color",
                Background = color == null
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2C, 0x2C, 0x34))
                    : (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(color)!
            };
            if (color == null)
                swatch.Child = new System.Windows.Controls.TextBlock
                {
                    Text = "✕",
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9A, 0x9A, 0xA6))
                };
            swatch.MouseLeftButtonDown += (_, _) =>
            {
                _selectedColor = swatch.Tag as string;
                UpdateSwatchSelection();
            };
            _swatches.Add(swatch);
            ColorRow.Children.Add(swatch);
        }
        UpdateSwatchSelection();
    }

    private void UpdateSwatchSelection()
    {
        foreach (var swatch in _swatches)
        {
            bool selected = string.Equals(swatch.Tag as string, _selectedColor, StringComparison.OrdinalIgnoreCase);
            swatch.BorderBrush = selected
                ? System.Windows.Media.Brushes.White
                : System.Windows.Media.Brushes.Transparent;
        }
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
