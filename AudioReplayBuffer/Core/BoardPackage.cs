using System.IO.Compression;
using System.Text.Json;

namespace AudioReplayBuffer.Core;

/// <summary>
/// Export/import of the soundboard as a shareable zip: all audio files
/// (with their category folder structure) plus a manifest carrying labels,
/// colors, volumes, pins, order and hotkeys.
/// </summary>
public static class BoardPackage
{
    private sealed class ManifestEntry
    {
        public string Rel { get; set; } = "";
        public string? Label { get; set; }
        public string? Color { get; set; }
        public int Volume { get; set; } = 100;
        public bool Pinned { get; set; }
        public int? Slot { get; set; }
        public string? Hotkey { get; set; }
    }

    private sealed class Manifest
    {
        public List<ManifestEntry> Sounds { get; set; } = [];
        public List<string> Order { get; set; } = [];
    }

    /// <summary>Returns the number of sounds exported.</summary>
    public static int Export(string libraryDir, SoundboardStore store, string zipPath)
    {
        var files = new DirectoryInfo(libraryDir)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => f.Extension is ".mp3" or ".wav")
            .ToList();

        var manifest = new Manifest();
        foreach (var file in files)
        {
            string rel = Path.GetRelativePath(libraryDir, file.FullName).Replace('\\', '/');
            manifest.Sounds.Add(new ManifestEntry
            {
                Rel = rel,
                Label = store.GetLabel(file.FullName),
                Color = store.GetColor(file.FullName),
                Volume = store.GetVolume(file.FullName),
                Pinned = store.IsPinned(file.FullName),
                Slot = store.SlotOf(file.FullName),
                Hotkey = store.GetHotkey(file.FullName)
            });
        }
        manifest.Order = files
            .OrderBy(f => store.OrderIndexOf(f.FullName))
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => Path.GetRelativePath(libraryDir, f.FullName).Replace('\\', '/'))
            .ToList();

        File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            string rel = Path.GetRelativePath(libraryDir, file.FullName).Replace('\\', '/');
            zip.CreateEntryFromFile(file.FullName, "sounds/" + rel);
        }
        var manifestEntry = zip.CreateEntry("board.json");
        using (var stream = manifestEntry.Open())
            JsonSerializer.Serialize(stream, manifest, new JsonSerializerOptions { WriteIndented = true });

        return files.Count;
    }

    /// <summary>
    /// Merges a package into the library. Existing files are never
    /// overwritten (collisions get " (1)" names); slots are applied only
    /// when free. Returns the number of sounds imported.
    /// </summary>
    public static int Import(string zipPath, string libraryDir, SoundboardStore store)
    {
        using var zip = ZipFile.OpenRead(zipPath);

        Manifest manifest = new();
        if (zip.GetEntry("board.json") is ZipArchiveEntry manifestEntry)
        {
            using var stream = manifestEntry.Open();
            manifest = JsonSerializer.Deserialize<Manifest>(stream) ?? new Manifest();
        }

        // Extract sounds, tracking rel → actual landed path.
        var landed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith("sounds/", StringComparison.OrdinalIgnoreCase) ||
                entry.Name.Length == 0)
                continue;
            string rel = entry.FullName["sounds/".Length..];
            string ext = Path.GetExtension(rel).ToLowerInvariant();
            if (ext is not (".mp3" or ".wav"))
                continue;

            // Sanitize: no absolute paths or traversal out of the library.
            string target = Path.GetFullPath(Path.Combine(libraryDir, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(Path.GetFullPath(libraryDir), StringComparison.OrdinalIgnoreCase))
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string unique = UniquePath(Path.GetDirectoryName(target)!,
                Path.GetFileNameWithoutExtension(target), ext);
            entry.ExtractToFile(unique);
            landed[rel] = unique;
        }

        // Apply metadata to the landed files.
        foreach (var sound in manifest.Sounds)
        {
            if (!landed.TryGetValue(sound.Rel, out string? path))
                continue;
            if (!string.IsNullOrWhiteSpace(sound.Label))
                store.SetLabel(path, sound.Label);
            if (!string.IsNullOrWhiteSpace(sound.Color))
                store.SetColor(path, sound.Color);
            if (sound.Volume != 100)
                store.SetVolume(path, sound.Volume);
            if (sound.Pinned)
                store.SetPinned(path, true);
            if (sound.Slot is int slot && store.PathOfSlot(slot) == null)
                store.AssignSlot(slot, path);
            if (!string.IsNullOrWhiteSpace(sound.Hotkey))
                store.SetHotkey(path, sound.Hotkey);
        }

        // Append imported sounds to the global order following the manifest.
        var currentOrder = new DirectoryInfo(libraryDir)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => f.Extension is ".mp3" or ".wav")
            .Select(f => f.FullName)
            .Where(p => !landed.ContainsValue(p))
            .OrderBy(store.OrderIndexOf)
            .ThenBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (string rel in manifest.Order)
            if (landed.TryGetValue(rel, out string? path))
                currentOrder.Add(path);
        foreach (var path in landed.Values)
            if (!currentOrder.Contains(path, StringComparer.OrdinalIgnoreCase))
                currentOrder.Add(path);
        store.SetOrder(currentOrder);

        return landed.Count;
    }

    private static string UniquePath(string folder, string baseName, string extension)
    {
        string path = Path.Combine(folder, baseName + extension);
        for (int i = 1; File.Exists(path); i++)
            path = Path.Combine(folder, $"{baseName} ({i}){extension}");
        return path;
    }
}
