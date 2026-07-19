using System.Windows;
using System.Windows.Input;
using AudioReplayBuffer.Core;

namespace AudioReplayBuffer.UI;

/// <summary>
/// Global quick launcher: summoned by a hotkey mid-game, type a few
/// letters, Enter fires the sound into the call. Searches the soundboard
/// library first, then recent replays.
/// </summary>
public partial class LauncherWindow : Window
{
    private sealed record Entry(string Title, string Sub, string FullPath);

    private readonly AppController _controller;
    private List<Entry> _all = [];

    public LauncherWindow(AppController controller)
    {
        _controller = controller;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            LoadEntries();
            Filter();
            Activate();
            QueryBox.Focus();
        };
    }

    private void LoadEntries()
    {
        var store = _controller.Soundboard;
        var entries = new List<Entry>();

        void AddFrom(string dir, string sub)
        {
            if (!Directory.Exists(dir))
                return;
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles()
                         .Where(f => f.Extension is ".mp3" or ".wav")
                         .OrderByDescending(f => f.LastWriteTime))
            {
                string title = store.GetLabel(f.FullName) ?? Path.GetFileNameWithoutExtension(f.Name);
                int? slot = store.SlotOf(f.FullName);
                entries.Add(new Entry(title, slot != null ? $"{sub} · ⌨{slot}" : sub, f.FullName));
            }
        }

        AddFrom(_controller.SoundLibraryDir, "soundboard");
        AddFrom(_controller.Settings.ResolveOutputFolder(), "replay");
        _all = entries;
    }

    private void Filter()
    {
        string query = QueryBox.Text.Trim();
        var matches = _all
            .Where(x => query.Length == 0 ||
                        x.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileName(x.FullPath).Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(8)
            .ToList();
        ResultsList.ItemsSource = matches;
        if (matches.Count > 0)
            ResultsList.SelectedIndex = 0;
    }

    private void OnQueryChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Filter();

    private void PlaySelected()
    {
        if (ResultsList.SelectedItem is Entry entry)
        {
            _controller.PlaySound(entry.FullPath);
            Close();
        }
    }

    private void OnResultClick(object sender, MouseButtonEventArgs e) => PlaySelected();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Enter:
                PlaySelected();
                e.Handled = true;
                break;
            case Key.Down when ResultsList.Items.Count > 0:
                ResultsList.SelectedIndex = Math.Min(ResultsList.SelectedIndex + 1, ResultsList.Items.Count - 1);
                e.Handled = true;
                break;
            case Key.Up when ResultsList.Items.Count > 0:
                ResultsList.SelectedIndex = Math.Max(ResultsList.SelectedIndex - 1, 0);
                e.Handled = true;
                break;
        }
    }

    private void OnDeactivated(object? sender, EventArgs e) => Close();
}
