namespace PianoPractice.Desktop.Models;

public sealed class LibraryItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string StoredFilePath { get; set; } = string.Empty;
    public string Composer { get; set; } = "Unknown Composer";
    public int MeasureCount { get; set; }
    public DateTimeOffset ImportedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastPlayedUtc { get; set; }
}
