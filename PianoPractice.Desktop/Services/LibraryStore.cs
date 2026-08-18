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
    private readonly string _libraryRootWithSeparator;
    private readonly string _trashDirectory;

    public LibraryStore(string? baseDir = null)
    {
        var appDir = Path.GetFullPath(baseDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CadenzaPianoStudio"));

        LibraryDirectory = Path.GetFullPath(Path.Combine(appDir, "Library"));
        ManifestPath = Path.GetFullPath(Path.Combine(appDir, "library_manifest.json"));
        _trashDirectory = Path.GetFullPath(Path.Combine(LibraryDirectory, ".Trash"));
        _libraryRootWithSeparator = LibraryDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        Directory.CreateDirectory(LibraryDirectory);
        Directory.CreateDirectory(_trashDirectory);
    }

    public string LibraryDirectory { get; }
    public string ManifestPath { get; }
    public IReadOnlyList<LibraryItem> Items => _items.AsReadOnly();

    public LibraryItem? GetItem(string id) =>
        _items.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));

    public List<LibraryItem> LoadLibrary()
    {
        _items.Clear();
        if (!File.Exists(ManifestPath))
            return _items;

        try
        {
            var content = File.ReadAllText(ManifestPath);
            var loaded = JsonSerializer.Deserialize<List<LibraryItem>>(content, JsonOptions);
            if (loaded is null)
            {
                PreserveCorruptManifest();
                return _items;
            }

            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in loaded)
            {
                if (string.IsNullOrWhiteSpace(item.Id) ||
                    !seenIds.Add(item.Id) ||
                    !TryResolveLibraryFile(item.StoredFilePath, out var safePath) ||
                    !File.Exists(safePath))
                    continue;

                item.StoredFilePath = safePath;
                _items.Add(item);
            }
        }
        catch (JsonException)
        {
            PreserveCorruptManifest();
        }
        catch (IOException)
        {
            // The caller can continue with an empty in-memory library.
        }
        catch (UnauthorizedAccessException)
        {
            // The caller can continue with an empty in-memory library.
        }

        return _items;
    }

    public LibraryItem AddOrUpdateFile(
        string sourceFilePath,
        string title,
        string composer = "Unknown Composer",
        int measureCount = 0) =>
        AddOrUpdateFile(sourceFilePath, title, composer, measureCount, null);

    public LibraryItem AddOrUpdateFile(
        string sourceFilePath,
        string title,
        string composer,
        int measureCount,
        string? contentSha256)
    {
        var sourceFullPath = Path.GetFullPath(sourceFilePath);
        if (!File.Exists(sourceFullPath))
            throw new FileNotFoundException("Source music file not found.", sourceFullPath);

        var originalFileName = Path.GetFileName(sourceFullPath);
        if (string.IsNullOrWhiteSpace(originalFileName))
            throw new InvalidDataException("The source file does not have a valid filename.");
        var normalizedHash = NormalizeContentHash(contentSha256);

        var existing = _items.FirstOrDefault(item =>
            (!string.IsNullOrWhiteSpace(normalizedHash) &&
             string.Equals(item.ContentSha256, normalizedHash, StringComparison.OrdinalIgnoreCase)) ||
            (TryResolveLibraryFile(item.StoredFilePath, out var storedPath) &&
             string.Equals(storedPath, sourceFullPath, StringComparison.OrdinalIgnoreCase)));

        if (existing is not null)
        {
            if (string.IsNullOrWhiteSpace(existing.DisplayName))
                existing.DisplayName = NormalizeDisplayName(title, originalFileName);
            if (!string.IsNullOrWhiteSpace(composer))
                existing.Composer = composer.Trim();
            if (measureCount > 0)
                existing.MeasureCount = measureCount;
            if (string.IsNullOrWhiteSpace(existing.ContentSha256))
                existing.ContentSha256 = normalizedHash;
            SaveManifest();
            return existing;
        }

        var extension = Path.GetExtension(originalFileName);
        var safeFileName = $"{Guid.NewGuid():N}{extension}";
        var destinationPath = Path.GetFullPath(Path.Combine(LibraryDirectory, safeFileName));
        EnsureInsideLibrary(destinationPath);
        File.Copy(sourceFullPath, destinationPath, overwrite: false);

        var newItem = new LibraryItem
        {
            Id = Guid.NewGuid().ToString("N"),
            DisplayName = NormalizeDisplayName(title, originalFileName),
            OriginalFileName = originalFileName,
            StoredFilePath = destinationPath,
            ContentSha256 = normalizedHash,
            Composer = string.IsNullOrWhiteSpace(composer) ? "Unknown Composer" : composer.Trim(),
            MeasureCount = measureCount,
            ImportedUtc = DateTimeOffset.UtcNow
        };

        _items.Insert(0, newItem);
        try
        {
            SaveManifest();
        }
        catch
        {
            _items.Remove(newItem);
            TryDeleteLibraryFile(destinationPath);
            throw;
        }

        return newItem;
    }

    public bool DeleteItem(string id)
    {
        var item = _items.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.Ordinal));
        if (item is null)
            return false;

        DeleteItems([id]);
        return true;
    }

    public int DeleteItems(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var targetIds = ids.ToHashSet(StringComparer.Ordinal);
        var toDelete = _items.Where(item => targetIds.Contains(item.Id)).ToArray();

        if (toDelete.Length == 0)
            return 0;

        var movedFiles = new List<(string Original, string Trash)>();
        var originalOrder = _items.ToArray();
        try
        {
            foreach (var item in toDelete)
            {
                if (TryResolveLibraryFile(item.StoredFilePath, out var sourcePath) && File.Exists(sourcePath))
                {
                    var trashPath = Path.Combine(
                        _trashDirectory,
                        $"{Guid.NewGuid():N}{Path.GetExtension(sourcePath)}");
                    File.Move(sourcePath, trashPath, overwrite: false);
                    movedFiles.Add((sourcePath, trashPath));
                }
                _items.Remove(item);
            }

            SaveManifest();
        }
        catch
        {
            _items.Clear();
            _items.AddRange(originalOrder);
            foreach (var moved in movedFiles.AsEnumerable().Reverse())
            {
                if (File.Exists(moved.Trash) && !File.Exists(moved.Original))
                    File.Move(moved.Trash, moved.Original, overwrite: false);
            }
            throw;
        }

        foreach (var moved in movedFiles)
            TryDeleteLibraryFile(moved.Trash);
        return toDelete.Length;
    }

    public bool RenameItem(string id, string newDisplayName)
    {
        if (string.IsNullOrWhiteSpace(newDisplayName))
            return false;

        var item = _items.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.Ordinal));
        if (item is null)
            return false;

        var originalName = item.DisplayName;
        item.DisplayName = newDisplayName.Trim();
        try
        {
            SaveManifest();
        }
        catch
        {
            item.DisplayName = originalName;
            throw;
        }
        return true;
    }

    public void RecordPlayed(string id)
    {
        var item = _items.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.Ordinal));
        if (item is null)
            return;

        var originalPlayed = item.LastPlayedUtc;
        item.LastPlayedUtc = DateTimeOffset.UtcNow;
        try
        {
            SaveManifest();
        }
        catch
        {
            item.LastPlayedUtc = originalPlayed;
            throw;
        }
    }

    public void SaveManifest()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
        var json = JsonSerializer.Serialize(_items, JsonOptions);
        var temporaryPath = ManifestPath + ".tmp";
        WriteDurableText(temporaryPath, json);
        ReplaceWithBackup(temporaryPath, ManifestPath, ManifestPath + ".bak");
    }

    private bool TryResolveLibraryFile(string? candidate, out string safePath)
    {
        safePath = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        try
        {
            var resolved = Path.GetFullPath(candidate);
            EnsureInsideLibrary(resolved);
            safePath = resolved;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException or
            InvalidOperationException)
        {
            return false;
        }
    }

    private void EnsureInsideLibrary(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(_libraryRootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The requested library file is outside Cadenza's managed library directory.");
    }

    private static string NormalizeContentHash(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit)
            ? normalized
            : string.Empty;
    }

    private static void WriteDurableText(string path, string value)
    {
        using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream);
        writer.Write(value);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static void ReplaceWithBackup(string temporaryPath, string destinationPath, string backupPath)
    {
        if (File.Exists(destinationPath))
            File.Replace(temporaryPath, destinationPath, backupPath, ignoreMetadataErrors: true);
        else
            File.Move(temporaryPath, destinationPath, overwrite: false);
    }

    private void TryDeleteLibraryFile(string? candidate)
    {
        if (!TryResolveLibraryFile(candidate, out var safePath))
            return;

        try
        {
            if (File.Exists(safePath))
                File.Delete(safePath);
        }
        catch (IOException)
        {
            // Keep manifest consistency even when Windows temporarily locks the file.
        }
        catch (UnauthorizedAccessException)
        {
            // Never attempt a fallback deletion outside the managed directory.
        }
    }

    private static string NormalizeDisplayName(string title, string originalFileName) =>
        string.IsNullOrWhiteSpace(title)
            ? Path.GetFileNameWithoutExtension(originalFileName)
            : title.Trim();

    private void PreserveCorruptManifest()
    {
        try
        {
            var backup = ManifestPath + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Move(ManifestPath, backup, overwrite: false);
        }
        catch
        {
            // The invalid manifest remains untouched if it cannot be moved.
        }
    }
}
