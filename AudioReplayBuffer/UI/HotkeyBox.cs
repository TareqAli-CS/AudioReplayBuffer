using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AudioReplayBuffer.UI;

/// <summary>
/// Keybind field, styled like a game's controls menu: shows "Click to set"
/// when empty, an accent highlight with "Press a combo…" while capturing,
/// the bold combo when set, and an ✕ button to remove it. The Hotkey
/// property always holds a string HotkeyManager can parse.
/// </summary>
public sealed class HotkeyBox : Control
{
    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey), typeof(string), typeof(HotkeyBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((HotkeyBox)d).UpdateDisplay()));

    public string Hotkey
    {
        get => (string)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value ?? "");
    }

    /// <summary>
    /// Raised when any HotkeyBox gains focus. The app suspends all global
    /// hotkeys during capture — otherwise pressing a combo that is
    /// currently bound would trigger it instead of being recorded.
    /// </summary>
    public static event Action? CaptureStarted;

    /// <summary>Raised when capture ends; the app re-registers its hotkeys.</summary>
    public static event Action? CaptureEnded;

    private TextBlock? _text;
    private Button? _clear;
    private string? _preview;

    public HotkeyBox()
    {
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.Hand;
        ToolTip = "Click, then press the key combo you want. ✕ or Backspace removes it.";
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _text = GetTemplateChild("PART_Text") as TextBlock;
        _clear = GetTemplateChild("PART_Clear") as Button;
        if (_clear != null)
            _clear.Click += (_, e) =>
            {
                Hotkey = "";
                e.Handled = true;
            };
        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        if (_text == null)
            return;
        bool focused = IsKeyboardFocusWithin;

        if (_preview != null)
        {
            _text.Text = _preview;
            _text.Opacity = 0.85;
        }
        else if (Hotkey.Length > 0)
        {
            _text.Text = Hotkey;
            _text.Opacity = 1.0;
        }
        else
        {
            _text.Text = focused ? "Press a combo…" : "Click to set";
            _text.Opacity = 0.45;
        }

        if (_clear != null)
            _clear.Visibility = Hotkey.Length > 0 && _preview == null
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        CaptureStarted?.Invoke();
        UpdateDisplay();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        _preview = null;
        base.OnLostKeyboardFocus(e);
        CaptureEnded?.Invoke();
        UpdateDisplay();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab)
            return; // keep keyboard navigation working
        e.Handled = true;

        switch (key)
        {
            case Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                 or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin:
                _preview = ModifierPrefix() + "…";
                UpdateDisplay();
                return;
            case Key.Back or Key.Delete:
                _preview = null;
                Hotkey = "";
                UpdateDisplay();
                return;
            case Key.Escape:
                _preview = null;
                UpdateDisplay();
                return;
        }

        bool isFunctionKey = key is >= Key.F1 and <= Key.F24;
        if (Keyboard.Modifiers == ModifierKeys.None && !isFunctionKey)
        {
            _preview = "Add Ctrl / Alt / Shift…";
            UpdateDisplay();
            return;
        }

        _preview = null;
        Hotkey = ModifierPrefix() + KeyName(key);
        UpdateDisplay();
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        // Modifiers released without completing a combo → drop the preview.
        if (_preview != null && Keyboard.Modifiers == ModifierKeys.None)
        {
            _preview = null;
            UpdateDisplay();
        }
        base.OnKeyUp(e);
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

    /// <summary>WPF Key → a name the hotkey parser accepts (digits stay pretty).</summary>
    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        Key.Return => "Enter",
        Key.Next => "PageDown",
        Key.Prior => "PageUp",
        Key.Capital => "CapsLock",
        _ => key.ToString()
    };
}
