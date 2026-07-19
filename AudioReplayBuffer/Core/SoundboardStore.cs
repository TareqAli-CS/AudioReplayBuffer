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
        foreach (var slot in _data.Slots.Where(kv =>
                     string.Equals(kv.Value, oldPath, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList())
            _data.Slots[slot] = newPath;
        Save();
    }

    public void RemoveFile(string path)
    {
        _data.Labels.Remove(path);
        foreach (var slot in _data.Slots.Where(kv =>
                     string.Equals(kv.Value, path, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList())
            _data.Slots.Remove(slot);
        Save();
    }
}
