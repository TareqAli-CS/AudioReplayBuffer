using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AudioReplayBuffer.UI;

/// <summary>
/// Registers system-wide hotkeys on an invisible message window and raises
/// <see cref="HotkeyPressed"/> with the id of the one that fired.
/// </summary>
public sealed class HotkeyManager : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public event Action<int>? HotkeyPressed;
    private readonly HashSet<int> _registered = [];

    public HotkeyManager() => CreateHandle(new CreateParams());

    /// <summary>
    /// Parses e.g. "Ctrl+Alt+S" or "F9" into modifier flags and a key, so
    /// callers can compare combos for conflicts before registering.
    /// </summary>
    public static bool TryParse(string hotkey, out uint modifiers, out Keys key, out string error)
    {
        modifiers = 0;
        key = Keys.None;

        foreach (var part in hotkey.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModControl; break;
                case "alt": modifiers |= ModAlt; break;
                case "shift": modifiers |= ModShift; break;
                case "win" or "windows": modifiers |= ModWin; break;
                default:
                    // Bare digits mean the number row (Keys.D0..D9); without
                    // this, Enum.TryParse would read "1" as a numeric enum value.
                    string keyName = part.Length == 1 && char.IsDigit(part[0]) ? "D" + part : part;
                    if (!Enum.TryParse(keyName, ignoreCase: true, out key))
                    {
                        error = $"Unknown key \"{part}\" in hotkey \"{hotkey}\".";
                        return false;
                    }
                    break;
            }
        }

        if (key == Keys.None)
        {
            error = $"Hotkey \"{hotkey}\" has no main key.";
            return false;
        }
        error = "";
        return true;
    }

    /// <summary>
    /// Registers a parsed combo globally under the given id, replacing
    /// whatever that id was bound to before.
    /// </summary>
    public bool TryRegister(int id, string hotkey, out string error)
    {
        if (_registered.Remove(id))
            UnregisterHotKey(Handle, id);

        if (!TryParse(hotkey, out uint modifiers, out Keys key, out error))
            return false;

        if (!RegisterHotKey(Handle, id, modifiers | ModNoRepeat, (uint)key))
        {
            error = $"Hotkey \"{hotkey}\" is already in use by another application.";
            return false;
        }

        _registered.Add(id);
        error = "";
        return true;
    }

    public void Unregister(int id)
    {
        if (_registered.Remove(id))
            UnregisterHotKey(Handle, id);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey)
            HotkeyPressed?.Invoke((int)m.WParam);
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (int id in _registered)
            UnregisterHotKey(Handle, id);
        _registered.Clear();
        DestroyHandle();
    }
}
