using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PianoPractice.Desktop.Models;

public sealed class MidiListenTrackOption : INotifyPropertyChanged
{
    private bool _isSelected;

    public MidiListenTrackOption(int trackIndex, string label, bool isSelected)
    {
        TrackIndex = trackIndex;
        Label = label;
        _isSelected = isSelected;
    }

    public int TrackIndex { get; }
    public string Label { get; }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
