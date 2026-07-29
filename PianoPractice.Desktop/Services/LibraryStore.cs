using System.IO;
using System.Text.Json;
using PianoPractice.Desktop.Models;

namespace PianoPractice.Desktop.Services;

public sealed class LibraryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly List<LibraryItem> _items = [];

    public LibraryStore(string? baseDir = null)
    {
        var appDir = baseDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CadenzaPianoStudio");

        LibraryDirectory = Path.Combine(appDir, "Library");
        ManifestPath = Path.Combine(appDir, "library_manifest.json");

        Directory.CreateDirectory(LibraryDirectory);
    }

    public string LibraryDirectory { get; }
    public string ManifestPath { get; }

    public IReadOnlyList<LibraryItem> Items => _items.AsReadOnly();

    public List<LibraryItem> LoadLibrary()
    {
        _items.Clear();
        try
        {
            if (File.Exists(ManifestPath))
            {
                var content = File.ReadAllText(ManifestPath);
                var loaded = JsonSerializer.Deserialize<List<LibraryItem>>(content, JsonOptions);
                if (loaded is not null)
                {
                    foreach (var item in loaded)
                    {
                        if (File.Exists(item.StoredFilePath))
                        {
                            _items.Add(item);
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            // Silently recover if manifest is missing or corrupt
        }

        return _items;
    }

    public LibraryItem AddOrUpdateFile(string sourceFilePath, string title, string composer = "Unknown Composer", int measureCount = 0)
    {
        if (!File.Exists(sourceFilePath))
            throw new FileNotFoundException("Source music file not found.", sourceFilePath);

        var originalFileName = Path.GetFileName(sourceFilePath);
        var extension = Path.GetExtension(sourceFilePath);

        // Check if an existing library item points to the same file or filename
        var existing = _items.FirstOrDefault(item =>
            string.Equals(item.StoredFilePath, sourceFilePath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.OriginalFileName, originalFileName, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.DisplayName = string.IsNullOrWhiteSpace(title) ? existing.DisplayName : title;
            existing.Composer = string.IsNullOrWhiteSpace(composer) ? existing.Composer : composer;
            if (measureCount > 0) existing.MeasureCount = measureCount;
            SaveManifest();
            return existing;
        }

        // Copy file to Cadenza's persistent Library directory
        var safeFileName = $"{Guid.NewGuid():N}_{originalFileName}";
        var destinationPath = Path.Combine(LibraryDirectory, safeFileName);
        File.Copy(sourceFilePath, destinationPath, overwrite: true);

        var newItem = new LibraryItem
        {
            Id = Guid.NewGuid().ToString("N"),
            DisplayName = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(originalFileName) : title,
            OriginalFileName = originalFileName,
            StoredFilePath = destinationPath,
            Composer = string.IsNullOrWhiteSpace(composer) ? "Unknown Composer" : composer,
            MeasureCount = measureCount,
            ImportedUtc = DateTimeOffset.UtcNow
        };

        _items.Insert(0, newItem);
        SaveManifest();
        return newItem;
    }

    public bool DeleteItem(string id)
    {
        var item = _items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));
        if (item is null) return false;

        _items.Remove(item);
        try
        {
            if (File.Exists(item.StoredFilePath))
            {
                File.Delete(item.StoredFilePath);
            }
        }
        catch (Exception)
        {
            // Best-effort file cleanup
        }

        SaveManifest();
        return true;
    }

    public int DeleteItems(IEnumerable<string> ids)
    {
        var targetIds = ids.ToHashSet(StringComparer.Ordinal);
        var toDelete = _items.Where(i => targetIds.Contains(i.Id)).ToList();
        int count = 0;

        foreach (var item in toDelete)
        {
            _items.Remove(item);
            try
            {
                if (File.Exists(item.StoredFilePath))
                {
                    File.Delete(item.StoredFilePath);
                }
            }
            catch (Exception)
            {
                // Best-effort cleanup
            }
            count++;
        }

        if (count > 0) SaveManifest();
        return count;
    }

    public bool RenameItem(string id, string newDisplayName)
    {
        if (string.IsNullOrWhiteSpace(newDisplayName)) return false;

        var item = _items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));
        if (item is null) return false;

        item.DisplayName = newDisplayName.Trim();
        SaveManifest();
        return true;
    }

    public void RecordPlayed(string id)
    {
        var item = _items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));
        if (item is not null)
        {
            item.LastPlayedUtc = DateTimeOffset.UtcNow;
            SaveManifest();
        }
    }

    public void SaveManifest()
    {
        try
        {
            var json = JsonSerializer.Serialize(_items, JsonOptions);
            var tmp = ManifestPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, ManifestPath, overwrite: true);
        }
        catch (Exception)
        {
            // Silently preserve stability
        }
    }
}
