using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PianoPractice.Desktop.Models;

public sealed class LibraryItemViewModel : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _displayName = string.Empty;
    private bool _isActiveScore;

    public LibraryItemViewModel(LibraryItem item)
    {
        Item = item;
        _displayName = item.DisplayName;
    }

    public LibraryItem Item { get; }

    public string Id => Item.Id;

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (_displayName == value) return;
            _displayName = value;
            Item.DisplayName = value;
            OnPropertyChanged();
        }
    }

    public string OriginalFileName => Item.OriginalFileName;
    public string StoredFilePath => Item.StoredFilePath;
    public string Composer => Item.Composer;
    public int MeasureCount => Item.MeasureCount;
    public DateTimeOffset ImportedUtc => Item.ImportedUtc;
    public DateTimeOffset? LastPlayedUtc => Item.LastPlayedUtc;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public bool IsActiveScore
    {
        get => _isActiveScore;
        set
        {
            if (_isActiveScore == value) return;
            _isActiveScore = value;
            OnPropertyChanged();
        }
    }

    public string ImportedDateLabel => $"Imported {ImportedUtc.LocalDateTime:MMM d, yyyy}";

    public string LastPlayedLabel => LastPlayedUtc.HasValue
        ? $"Played {LastPlayedUtc.Value.LocalDateTime:MMM d, h:mm tt}"
        : "Not played yet";

    public string SubtitleLabel => $"{MeasureCount} measures · {Composer}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
