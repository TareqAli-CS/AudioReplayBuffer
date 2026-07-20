using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ReplayPad.UI;

/// <summary>Category chooser used by "Move / copy to category…" on pads.</summary>
public partial class CategoryPickerDialog : Window
{
    private const string NoCategoryItem = "(No category — main board)";
    private const string NewCategoryItem = "➕  New category…";

    public bool Confirmed { get; private set; }
    public bool IsCopy { get; private set; }
    public string? Category { get; private set; }

    public CategoryPickerDialog(IReadOnlyList<string> categories, string? currentCategory)
    {
        InitializeComponent();
        CategoryList.Items.Add(NoCategoryItem);
        foreach (string category in categories)
            CategoryList.Items.Add(category);
        CategoryList.Items.Add(NewCategoryItem);
        CategoryList.SelectedItem = currentCategory != null && categories.Contains(currentCategory)
            ? currentCategory
            : NoCategoryItem;
    }

    private bool ResolveSelection()
    {
        if (CategoryList.SelectedItem is not string selected)
            return false;
        if (selected == NewCategoryItem)
        {
            var prompt = new InputDialog("New category", "Category name (e.g. memes, music)") { Owner = this };
            prompt.ShowDialog();
            if (prompt.Result is not string name || name.Length == 0)
                return false;
            Category = name;
            return true;
        }
        Category = selected == NoCategoryItem ? null : selected;
        return true;
    }

    private void OnMoveClick(object sender, RoutedEventArgs e)
    {
        if (!ResolveSelection())
            return;
        IsCopy = false;
        Confirmed = true;
        Close();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (!ResolveSelection())
            return;
        IsCopy = true;
        Confirmed = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        int dark = 1;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
    }
}
