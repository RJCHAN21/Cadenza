using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using PianoPractice.Desktop.Models;
using PianoPractice.Desktop.Services;

namespace PianoPractice.Desktop;

public partial class MainWindow
{
    private StrictEndBarBoundaryGuard? _strictEndBarBoundaryGuard;
    private FocusRangeRetentionGuard? _focusRangeRetentionGuard;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        _strictEndBarBoundaryGuard ??= new StrictEndBarBoundaryGuard(_viewModel, Dispatcher);
        _focusRangeRetentionGuard ??= new FocusRangeRetentionGuard(_viewModel, Dispatcher);
    }

    protected override void OnClosed(EventArgs e)
    {
        _focusRangeRetentionGuard?.Dispose();
        _focusRangeRetentionGuard = null;
        _strictEndBarBoundaryGuard?.Dispose();
        _strictEndBarBoundaryGuard = null;
        base.OnClosed(e);
    }

    private sealed class FocusRangeRetentionGuard : IDisposable
    {
        private readonly MainWindowViewModel _viewModel;
        private readonly Dispatcher _dispatcher;
        private long _generation;
        private bool _disposed;

        public FocusRangeRetentionGuard(MainWindowViewModel viewModel, Dispatcher dispatcher)
        {
            _viewModel = viewModel;
            _dispatcher = dispatcher;
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _generation++;
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_disposed || e.PropertyName != nameof(MainWindowViewModel.FocusStartMeasure))
                return;

            // FocusEndMeasure exposes the effective value. A stored value of zero means
            // "through the final bar," so it must not be compared as if bar zero were selected.
            var preservedEndBar = _viewModel.FocusEndMeasure;
            var requestedStartBar = _viewModel.FocusStartMeasure;
            if (preservedEndBar < requestedStartBar)
                return;

            var generation = ++_generation;
            _dispatcher.BeginInvoke(
                DispatcherPriority.DataBind,
                new Action(() => RestoreEndBarAfterStartUpdate(generation, preservedEndBar)));
        }

        private void RestoreEndBarAfterStartUpdate(long generation, int preservedEndBar)
        {
            if (_disposed || generation != _generation)
                return;

            if (preservedEndBar < _viewModel.FocusStartMeasure ||
                _viewModel.FocusEndMeasure == preservedEndBar)
            {
                return;
            }

            _viewModel.FocusEndMeasure = preservedEndBar;
        }
    }

    private sealed class StrictEndBarBoundaryGuard : IDisposable
    {
        private const double BeatEpsilon = 0.002;

        private readonly MainWindowViewModel _viewModel;
        private readonly Dispatcher _dispatcher;
        private CancellationTokenSource? _completionCancellation;
        private PlaybackSelectionBoundary? _activeBoundary;
        private long _generation;
        private bool _completing;
        private bool _disposed;

        public StrictEndBarBoundaryGuard(MainWindowViewModel viewModel, Dispatcher dispatcher)
        {
            _viewModel = viewModel;
            _dispatcher = dispatcher;
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            Reevaluate();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            CancelScheduledCompletion();
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_disposed || _completing)
                return;

            if (e.PropertyName == nameof(MainWindowViewModel.CursorBeat))
            {
                if (ShouldGuardPreview() &&
                    _activeBoundary is { } boundary &&
                    _viewModel.CursorBeat >= boundary.EndBeat - BeatEpsilon)
                {
                    QueueImmediateCompletion();
                }
                return;
            }

            if (e.PropertyName is nameof(MainWindowViewModel.IsPreviewPlaying)
                or nameof(MainWindowViewModel.IsPreviewPaused)
                or nameof(MainWindowViewModel.IsScorePreviewPlaying)
                or nameof(MainWindowViewModel.CurrentScore)
                or nameof(MainWindowViewModel.FocusStartMeasure)
                or nameof(MainWindowViewModel.FocusEndMeasure)
                or nameof(MainWindowViewModel.SelectedLessonMode)
                or nameof(MainWindowViewModel.EffectiveLessonTempoBpm))
            {
                Reevaluate();
            }
        }

        private bool ShouldGuardPreview() =>
            !_disposed &&
            _viewModel.CurrentScore is not null &&
            _viewModel.SelectedLessonMode == LessonMode.Listen &&
            _viewModel.IsScorePreviewPlaying;

        private void Reevaluate()
        {
            CancelScheduledCompletion();
            _activeBoundary = null;

            if (!ShouldGuardPreview() || _viewModel.CurrentScore is not { } score)
                return;

            var boundary = PlaybackSelectionBoundaryResolver.Resolve(
                score,
                _viewModel.FocusStartMeasure,
                _viewModel.FocusEndMeasure,
                _viewModel.CursorBeat);
            _activeBoundary = boundary;

            if (_viewModel.CursorBeat >= boundary.EndBeat - BeatEpsilon)
            {
                QueueImmediateCompletion();
                return;
            }

            var remaining = PlaybackSelectionBoundaryResolver.RemainingDuration(
                score,
                boundary,
                _viewModel.CursorBeat,
                _viewModel.EffectiveLessonTempoBpm);

            var cancellation = new CancellationTokenSource();
            _completionCancellation = cancellation;
            var generation = ++_generation;
            _ = CompleteAfterDelayAsync(remaining, generation, cancellation.Token);
        }

        private async Task CompleteAfterDelayAsync(
            TimeSpan remaining,
            long generation,
            CancellationToken cancellationToken)
        {
            try
            {
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, cancellationToken);

                await _dispatcher.InvokeAsync(
                    () => CompleteAtBoundary(generation),
                    DispatcherPriority.Send,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void QueueImmediateCompletion()
        {
            var generation = _generation;
            _dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(() => CompleteAtBoundary(generation)));
        }

        private void CompleteAtBoundary(long generation)
        {
            if (_disposed || _completing || generation != _generation ||
                !ShouldGuardPreview() || _activeBoundary is not { } boundary)
            {
                return;
            }

            _completing = true;
            try
            {
                _viewModel.CursorBeat = boundary.EndBeat;
                if (_viewModel.IsLoopEnabled)
                {
                    _ = RestartLoopAsync();
                }
                else
                {
                    _viewModel.StopPreview(resetToStart: false);
                }
            }
            finally
            {
                _completing = false;
            }
        }

        private async Task RestartLoopAsync()
        {
            try
            {
                await _viewModel.RestartPreviewAsync();
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void CancelScheduledCompletion()
        {
            _generation++;
            var cancellation = Interlocked.Exchange(ref _completionCancellation, null);
            if (cancellation is null)
                return;

            cancellation.Cancel();
            cancellation.Dispose();
        }
    }
}
