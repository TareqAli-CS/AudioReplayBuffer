using System.Text.Json;

namespace AudioReplayBuffer.Core;

/// <summary>
/// Persistent labels and soundboard slot assignments for replay files,
/// stored in soundboard.json next to the exe. All access happens on the
/// UI thread (hotkeys arrive via the message window on the same thread).
/// </summary>
public sealed class SoundboardStore
{
    public const int SlotCount = 9;

    private sealed class StoreData
    {
        public Dictionary<string, string> Labels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<int, string> Slots { get; set; } = [];
        public Dictionary<string, int> Volumes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Colors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Pinned { get; set; } = [];
    }

    private static string StorePath => AppPaths.SoundboardPath;
    private readonly StoreData _data;

    public SoundboardStore()
    {
        try
        {
            _data = File.Exists(StorePath)
                ? JsonSerializer.Deserialize<StoreData>(File.ReadAllText(StorePath)) ?? new StoreData()
                : new StoreData();
        }
        catch (Exception ex)
        {
            Logger.Log("Could not load soundboard.json, starting fresh: " + ex.Message);
            _data = new StoreData();
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Logger.Log("Could not save soundboard.json: " + ex.Message);
        }
    }

    public string? GetLabel(string path)
        => _data.Labels.TryGetValue(path, out var label) && label.Length > 0 ? label : null;

    public void SetLabel(string path, string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            _data.Labels.Remove(path);
        else
            _data.Labels[path] = label.Trim();
        Save();
    }

    /// <summary>Per-sound playback volume in percent (100 = unchanged).</summary>
    public int GetVolume(string path)
        => _data.Volumes.TryGetValue(path, out int volume) ? volume : 100;

    public void SetVolume(string path, int percent)
    {
        if (percent == 100)
            _data.Volumes.Remove(path);
        else
            _data.Volumes[path] = Math.Clamp(percent, 10, 300);
        Save();
    }

    /// <summary>Pad accent color as "#RRGGBB", or null for none.</summary>
    public string? GetColor(string path)
        => _data.Colors.TryGetValue(path, out var color) ? color : null;

    public void SetColor(string path, string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
            _data.Colors.Remove(path);
        else
            _data.Colors[path] = color;
        Save();
    }

    public bool IsPinned(string path)
        => _data.Pinned.Contains(path, StringComparer.OrdinalIgnoreCase);

    public void SetPinned(string path, bool pinned)
    {
        _data.Pinned.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (pinned)
            _data.Pinned.Add(path);
        Save();
    }

    public int? SlotOf(string path)
    {
        foreach (var (slot, slotPath) in _data.Slots)
            if (string.Equals(slotPath, path, StringComparison.OrdinalIgnoreCase))
                return slot;
        return null;
    }

    public string? PathOfSlot(int slot)
        => _data.Slots.TryGetValue(slot, out var path) ? path : null;

    public bool AnySlotAssigned => _data.Slots.Count > 0;

    /// <summary>Puts the file in a slot (null = unassign it from every slot).</summary>
    public void AssignSlot(int? slot, string path)
    {
        foreach (var s in _data.Slots.Where(kv =>
                     string.Equals(kv.Value, path, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList())
            _data.Slots.Remove(s);
        if (slot is int n && n >= 1 && n <= SlotCount)
            _data.Slots[n] = path;
        Save();
    }

    public void RenameFile(string oldPath, string newPath)
    {
        if (_data.Labels.Remove(oldPath, out var label))
            _data.Labels[newPath] = label;
        if (_data.Volumes.Remove(oldPath, out int volume))
            _data.Volumes[newPath] = volume;
        if (_data.Colors.Remove(oldPath, out var color))
            _data.Colors[newPath] = color;
        if (_data.Pinned.RemoveAll(p => string.Equals(p, oldPath, StringComparison.OrdinalIgnoreCase)) > 0)
            _data.Pinned.Add(newPath);
        foreach (var slot in _data.Slots.Where(kv =>
                     string.Equals(kv.Value, oldPath, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList())
            _data.Slots[slot] = newPath;
        Save();
    }

    public void RemoveFile(string path)
    {
        _data.Labels.Remove(path);
        _data.Volumes.Remove(path);
        _data.Colors.Remove(path);
        _data.Pinned.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        foreach (var slot in _data.Slots.Where(kv =>
                     string.Equals(kv.Value, path, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList())
            _data.Slots.Remove(slot);
        Save();
    }
}
