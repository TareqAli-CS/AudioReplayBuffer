using System.Windows.Controls;
using System.Windows.Input;

namespace AudioReplayBuffer.UI;

/// <summary>
/// A TextBox that records a key combo by pressing it instead of typing it.
/// Click → press e.g. Ctrl+Shift+B → the box shows "Ctrl+Shift+B" (its Text
/// is always a string HotkeyManager can parse). Backspace/Delete clears,
/// Esc reverts, a held modifier shows a live "Ctrl+…" preview. Bare keys
/// are rejected (except F1–F24) so a global hotkey can't hijack normal
/// typing.
/// </summary>
public sealed class HotkeyBox : TextBox
{
    private string _committed = "";
    private bool _selfChange;

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = Cursors.Hand;
        ContextMenu = null;
        ToolTip = "Click, then press the key combo you want (e.g. Ctrl+Shift+B or F9). Backspace removes it.";
    }

    protected override void OnTextChanged(TextChangedEventArgs e)
    {
        base.OnTextChanged(e);
        // External assignments (dialog load) are the committed value;
        // our own transient previews are not.
        if (!_selfChange)
            _committed = Text;
    }

    private void SetText(string text)
    {
        _selfChange = true;
        Text = text;
        _selfChange = false;
    }

    private void Commit(string combo)
    {
        _committed = combo;
        SetText(combo);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        switch (key)
        {
            case Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                 or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin:
                SetText(ModifierPrefix() + "…");
                return;
            case Key.Back or Key.Delete:
                Commit("");
                return;
            case Key.Escape:
                SetText(_committed);
                return;
            case Key.Tab:
                e.Handled = false; // keep keyboard navigation working
                return;
        }

        bool isFunctionKey = key is >= Key.F1 and <= Key.F24;
        if (Keyboard.Modifiers == ModifierKeys.None && !isFunctionKey)
        {
            SetText("Add Ctrl / Alt / Shift…");
            return;
        }

        Commit(ModifierPrefix() + KeyName(key));
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        // Modifiers released without completing a combo → drop the preview.
        if (Keyboard.Modifiers == ModifierKeys.None && Text != _committed)
            SetText(_committed);
        base.OnPreviewKeyUp(e);
    }

    protected override void OnLostKeyboardFocus(System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        SetText(_committed);
        base.OnLostKeyboardFocus(e);
    }

    private static string ModifierPrefix()
    {
        var mods = Keyboard.Modifiers;
        string prefix = "";
        if (mods.HasFlag(ModifierKeys.Control)) prefix += "Ctrl+";
        if (mods.HasFlag(ModifierKeys.Alt)) prefix += "Alt+";
        if (mods.HasFlag(ModifierKeys.Shift)) prefix += "Shift+";
        if (mods.HasFlag(ModifierKeys.Windows)) prefix += "Win+";
        return prefix;
    }

    /// <summary>WPF Key → a name the WinForms-based hotkey parser accepts.</summary>
    private static string KeyName(Key key) => key switch
    {
        Key.Return => "Enter",
        Key.Next => "PageDown",
        Key.Prior => "PageUp",
        Key.Capital => "CapsLock",
        _ => key.ToString()
    };
}
