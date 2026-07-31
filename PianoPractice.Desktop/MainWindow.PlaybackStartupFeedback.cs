using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PianoPractice.Desktop.Models;

namespace PianoPractice.Desktop;

public partial class MainWindow
{
    private Border? _playbackStartupPanel;
    private TextBlock? _playbackStartupText;
    private Button? _playbackTransportButton;
    private DispatcherTimer? _playbackStartupTimer;
    private readonly Stopwatch _playbackStartupClock = new();
    private bool _playbackStartupFeedbackInstalled;
    private bool _performanceCountdownActive;
    private bool _playbackPreparationOwnsCursor;
    private Cursor? _cursorBeforePlaybackPreparation;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_playbackStartupFeedbackInstalled) return;

        _playbackStartupFeedbackInstalled = true;
        InstallPlaybackStartupFeedback();
        _viewModel.PropertyChanged += ViewModel_PlaybackStartupPropertyChanged;
        _viewModel.CountdownStepRequested += ViewModel_PlaybackCountdownStarted;
        _viewModel.HideCountdownRequested += ViewModel_PlaybackCountdownEnded;
        UpdatePlaybackStartupFeedback();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_playbackStartupFeedbackInstalled)
        {
            _viewModel.PropertyChanged -= ViewModel_PlaybackStartupPropertyChanged;
            _viewModel.CountdownStepRequested -= ViewModel_PlaybackCountdownStarted;
            _viewModel.HideCountdownRequested -= ViewModel_PlaybackCountdownEnded;
        }

        _playbackStartupTimer?.Stop();
        RestorePlaybackPreparationCursor();
        base.OnClosed(e);
    }

    private void InstallPlaybackStartupFeedback()
    {
        _playbackTransportButton = FindVisualDescendants<Button>(this)
            .FirstOrDefault(button =>
            {
                var binding = BindingOperations.GetBindingExpression(
                    button,
                    ContentControl.ContentProperty);
                return string.Equals(
                    binding?.ParentBinding.Path?.Path,
                    nameof(MainWindowViewModel.PreviewButtonLabel),
                    StringComparison.Ordinal);
            });

        if (_playbackTransportButton is not null)
        {
            var labelBinding = new MultiBinding
            {
                Mode = BindingMode.OneWay,
                Converter = new PlaybackTransportLabelConverter(this)
            };
            labelBinding.Bindings.Add(new Binding(nameof(MainWindowViewModel.PreviewButtonLabel)));
            labelBinding.Bindings.Add(new Binding(nameof(MainWindowViewModel.LessonButtonLabel)));
            _playbackTransportButton.SetBinding(ContentControl.ContentProperty, labelBinding);
        }

        if (Content is not Grid root) return;

        _playbackStartupText = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 7)
        };

        var progress = new ProgressBar
        {
            Height = 3,
            Width = 280,
            IsIndeterminate = true,
            BorderThickness = new Thickness(0)
        };

        var stack = new StackPanel();
        stack.Children.Add(_playbackStartupText);
        stack.Children.Add(progress);

        _playbackStartupPanel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(238, 19, 25, 34)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(50, 61, 76)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18, 12, 18, 12),
            Child = stack,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 88),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        Panel.SetZIndex(_playbackStartupPanel, 1000);
        root.Children.Add(_playbackStartupPanel);

        _playbackStartupTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _playbackStartupTimer.Tick += (_, _) => UpdatePlaybackStartupText();
    }

    private void ViewModel_PlaybackStartupPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.IsPreviewBuilding)
            or nameof(MainWindowViewModel.PreviewButtonLabel)
            or nameof(MainWindowViewModel.LessonButtonLabel)
            or nameof(MainWindowViewModel.SelectedLessonMode))
        {
            UpdatePlaybackStartupFeedback();
        }
    }

    private void ViewModel_PlaybackCountdownStarted(object? sender, string step)
    {
        _performanceCountdownActive = true;
        UpdatePlaybackStartupFeedback();
        RefreshPlaybackTransportLabel();
    }

    private void ViewModel_PlaybackCountdownEnded(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(
            () =>
            {
                _performanceCountdownActive = false;
                UpdatePlaybackStartupFeedback();
                RefreshPlaybackTransportLabel();
            },
            DispatcherPriority.ContextIdle);
    }

    private bool IsPlaybackPreparing()
    {
        if (_performanceCountdownActive) return false;
        return _viewModel.IsPreviewBuilding ||
               _viewModel.LessonButtonLabel.StartsWith(
                   "Preparing",
                   StringComparison.OrdinalIgnoreCase);
    }

    private void UpdatePlaybackStartupFeedback()
    {
        if (_playbackStartupPanel is null) return;

        var preparing = IsPlaybackPreparing();
        _playbackStartupPanel.Visibility =
            preparing ? Visibility.Visible : Visibility.Collapsed;
        RefreshPlaybackTransportLabel();

        if (preparing)
        {
            if (!_playbackStartupClock.IsRunning)
            {
                _playbackStartupClock.Restart();
                _playbackStartupTimer?.Start();
                if (!_playbackPreparationOwnsCursor)
                {
                    _cursorBeforePlaybackPreparation = Cursor;
                    _playbackPreparationOwnsCursor = true;
                    Cursor = Cursors.Wait;
                }
            }

            UpdatePlaybackStartupText();
        }
        else
        {
            _playbackStartupTimer?.Stop();
            _playbackStartupClock.Reset();
            RestorePlaybackPreparationCursor();
        }
    }

    private void UpdatePlaybackStartupText()
    {
        if (_playbackStartupText is null || !IsPlaybackPreparing()) return;

        var label = _viewModel.IsPreviewBuilding
            ? "Preparing Listen playback"
            : _viewModel.SelectedLessonMode switch
            {
                LessonMode.TimedPlay => "Preparing synchronized Performance audio",
                LessonMode.WaitForYou => "Starting Practice mode",
                _ => "Preparing Listen playback"
            };

        _playbackStartupText.Text =
            $"{label} · {_playbackStartupClock.Elapsed.TotalSeconds:0.0}s";
    }

    private void RefreshPlaybackTransportLabel()
    {
        if (_playbackTransportButton is null) return;
        BindingOperations.GetMultiBindingExpression(
            _playbackTransportButton,
            ContentControl.ContentProperty)?.UpdateTarget();
    }

    private void RestorePlaybackPreparationCursor()
    {
        if (!_playbackPreparationOwnsCursor) return;
        Cursor = _cursorBeforePlaybackPreparation;
        _cursorBeforePlaybackPreparation = null;
        _playbackPreparationOwnsCursor = false;
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualDescendants<T>(child))
                yield return descendant;
        }
    }

    private sealed class PlaybackTransportLabelConverter : IMultiValueConverter
    {
        private readonly MainWindow _owner;

        public PlaybackTransportLabelConverter(MainWindow owner)
        {
            _owner = owner;
        }

        public object Convert(
            object[] values,
            Type targetType,
            object parameter,
            CultureInfo culture)
        {
            var previewLabel = values.Length > 0
                ? values[0]?.ToString()
                : null;
            var lessonLabel = values.Length > 1
                ? values[1]?.ToString()
                : null;

            if (_owner._performanceCountdownActive) return "Starting...";
            return lessonLabel?.StartsWith(
                       "Preparing",
                       StringComparison.OrdinalIgnoreCase) == true
                ? "Preparing..."
                : previewLabel ?? "Play";
        }

        public object[] ConvertBack(
            object value,
            Type[] targetTypes,
            object parameter,
            CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
