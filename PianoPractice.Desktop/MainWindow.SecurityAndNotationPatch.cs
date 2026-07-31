using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using PianoPractice.Desktop.Models;

namespace PianoPractice.Desktop;

public partial class MainWindow
{
    private bool _runtimeHardeningInstalled;
    private string? _runtimePatchScript;
    private readonly Stopwatch _correctedPerformanceClock = new();
    private bool _correctedClockActive;
    private double _correctedClockAnchorBeat;
    private double _lastCorrectedBeat;

    static MainWindow()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            LoadedEvent,
            new RoutedEventHandler(MainWindow_RuntimeHardeningLoaded),
            handledEventsToo: true);
    }

    private static void MainWindow_RuntimeHardeningLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is MainWindow window)
            _ = window.Dispatcher.InvokeAsync(window.InstallRuntimeHardeningAsync, DispatcherPriority.Loaded);
    }

    private async Task InstallRuntimeHardeningAsync()
    {
        if (_runtimeHardeningInstalled)
            return;

        for (var attempt = 0; attempt < 200 && NotationWebView.CoreWebView2 is null; attempt++)
            await Task.Delay(25);

        var core = NotationWebView.CoreWebView2;
        if (core is null || _runtimeHardeningInstalled)
            return;

        _runtimeHardeningInstalled = true;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsWebMessageEnabled = true;

        core.NavigationStarting += HardenedNavigationStarting;
        core.FrameNavigationStarting += HardenedNavigationStarting;
        core.NewWindowRequested += HardenedNewWindowRequested;
        core.PermissionRequested += HardenedPermissionRequested;
        core.DownloadStarting += HardenedDownloadStarting;
        core.NavigationCompleted += HardenedNavigationCompleted;

        // Replace—not supplement—the original message subscription. This ensures
        // every message is origin-checked before any native action or file write.
        core.WebMessageReceived -= NotationWebView_WebMessageReceived;
        core.WebMessageReceived += TrustedNotationWebMessageReceived;

        CompositionTarget.Rendering += RuntimeCorrectedRendering;
        Closed += RuntimeHardeningClosed;

        var patchDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "Verovio");
        var patchPaths = new[]
        {
            Path.Combine(patchDirectory, "cadenza-runtime-patch.js"),
            Path.Combine(patchDirectory, "cadenza-runtime-edge-patch.js")
        };
        var patchScripts = new List<string>();
        foreach (var patchPath in patchPaths)
        {
            if (!File.Exists(patchPath))
            {
                _viewModel.SetStatusMessage($"The notation safety patch {Path.GetFileName(patchPath)} is missing from the application output.");
                return;
            }
            patchScripts.Add(await File.ReadAllTextAsync(patchPath));
        }

        _runtimePatchScript = string.Join(Environment.NewLine, patchScripts);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(_runtimePatchScript);
        await ApplyRuntimePatchToCurrentDocumentAsync();
    }

    private void RuntimeHardeningClosed(object? sender, EventArgs args)
    {
        CompositionTarget.Rendering -= RuntimeCorrectedRendering;
        _correctedPerformanceClock.Stop();
    }

    private void RuntimeCorrectedRendering(object? sender, EventArgs args)
    {
        var score = _viewModel.CurrentScore;
        var clockRequired = score is not null &&
            ((_viewModel.IsPreviewPlaying && !_viewModel.IsLessonActive) ||
             (_viewModel.IsLessonActive && _viewModel.SelectedLessonMode == LessonMode.TimedPlay));

        if (!clockRequired || score is null)
        {
            _correctedClockActive = false;
            _correctedPerformanceClock.Reset();
            return;
        }

        var observedBeat = Math.Clamp(_viewModel.CursorBeat, 0, score.TotalPerformanceBeats);
        if (!_correctedClockActive || Math.Abs(observedBeat - _lastCorrectedBeat) > 0.75)
        {
            _correctedClockActive = true;
            _correctedClockAnchorBeat = observedBeat;
            _lastCorrectedBeat = observedBeat;
            _correctedPerformanceClock.Restart();
        }

        var tempoScale = Math.Max(0.01, _viewModel.EffectiveLessonTempoBpm / Math.Max(1d, score.TempoBpm));
        var anchorSeconds = score.SecondsAtPerformanceBeat(_correctedClockAnchorBeat, tempoScale);
        var targetBeat = score.PerformanceBeatAtSeconds(
            anchorSeconds + _correctedPerformanceClock.Elapsed.TotalSeconds,
            tempoScale);
        var rangeEnd = SelectedPerformanceRangeEnd(score);
        targetBeat = Math.Clamp(targetBeat, _correctedClockAnchorBeat, rangeEnd);

        _viewModel.CursorBeat = targetBeat;
        _lastCorrectedBeat = targetBeat;
    }

    private double SelectedPerformanceRangeEnd(ScoreDocument score)
    {
        var endMeasure = _viewModel.FocusEndMeasure <= 0
            ? score.MeasureCount
            : _viewModel.FocusEndMeasure;
        return score.PerformanceMeasures
            .Where(occurrence =>
                int.TryParse(occurrence.MeasureNumber, out var measure) &&
                measure >= _viewModel.FocusStartMeasure &&
                measure <= endMeasure)
            .Select(occurrence => occurrence.PerformanceStartBeat + occurrence.DurationBeats)
            .DefaultIfEmpty(score.TotalPerformanceBeats)
            .Max();
    }

    private static void HardenedNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs args)
    {
        if (!IsTrustedRendererUri(args.Uri))
            args.Cancel = true;
    }

    private static void HardenedNewWindowRequested(
        object? sender,
        CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
    }

    private static void HardenedPermissionRequested(
        object? sender,
        CoreWebView2PermissionRequestedEventArgs args)
    {
        args.State = CoreWebView2PermissionState.Deny;
    }

    private static void HardenedDownloadStarting(
        object? sender,
        CoreWebView2DownloadStartingEventArgs args)
    {
        args.Cancel = true;
    }

    private async void HardenedNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!args.IsSuccess)
            return;

        await ApplyRuntimePatchToCurrentDocumentAsync();
        await SynchronizePerformanceModelAsync();
    }

    private async void TrustedNotationWebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!IsTrustedRendererUri(args.Source))
            return;

        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            if (document.RootElement.TryGetProperty("type", out var typeProperty) &&
                string.Equals(typeProperty.GetString(), "rendered", StringComparison.Ordinal))
            {
                await ApplyRuntimePatchToCurrentDocumentAsync();
                await SynchronizePerformanceModelAsync();
            }
        }
        catch (JsonException)
        {
            _viewModel.SetStatusMessage("Notation engine sent a malformed message, which was ignored.");
            return;
        }

        // The existing handler remains the single behavior dispatcher, but it is
        // now reachable only after the trusted-origin and JSON checks above.
        NotationWebView_WebMessageReceived(sender, args);
    }

    private async Task ApplyRuntimePatchToCurrentDocumentAsync()
    {
        var core = NotationWebView.CoreWebView2;
        if (core is null || string.IsNullOrWhiteSpace(_runtimePatchScript))
            return;
        if (!IsTrustedRendererUri(core.Source))
            return;

        try
        {
            await core.ExecuteScriptAsync(_runtimePatchScript);
        }
        catch (InvalidOperationException)
        {
            // Navigation can replace the document between the source check and execution.
        }
    }

    private async Task SynchronizePerformanceModelAsync()
    {
        var core = NotationWebView.CoreWebView2;
        var score = _viewModel.CurrentScore;
        if (core is null || score is null || !IsTrustedRendererUri(core.Source))
            return;

        var timeline = JsonSerializer.Serialize(score.PerformanceMeasures.Select(occurrence => new
        {
            occurrenceIndex = occurrence.OccurrenceIndex,
            sourceMeasureIndex = occurrence.SourceMeasureIndex,
            measureNumber = occurrence.MeasureNumber,
            sourceStartBeat = occurrence.SourceStartBeat,
            performanceStartBeat = occurrence.PerformanceStartBeat,
            durationBeats = occurrence.DurationBeats,
            repeatPass = occurrence.RepeatPass,
            repeatSectionId = occurrence.RepeatSectionId
        }));
        var tempoChanges = JsonSerializer.Serialize(score.TempoChanges.Select(change => new
        {
            performanceBeat = change.PerformanceBeat,
            bpm = change.Bpm
        }));

        var totalBeats = score.TotalPerformanceBeats.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var initialBpm = score.TempoBpm.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var cursorBeat = _viewModel.CursorBeat.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var script =
            $"window.CadenzaNotation?.setPerformanceTimeline({timeline});" +
            $"window.CadenzaNotation?.setPerformanceClock?.({tempoChanges},{totalBeats},{initialBpm});" +
            $"window.CadenzaNotation?.setCursorBeat({cursorBeat},true);";

        try
        {
            await core.ExecuteScriptAsync(script);
        }
        catch (InvalidOperationException)
        {
            // A new score/navigation will perform synchronization again.
        }
    }

    private static bool IsTrustedRendererUri(string? value)
    {
        if (string.Equals(value, "about:blank", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return false;
        return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(uri.Host, "cadenza.local", StringComparison.OrdinalIgnoreCase) &&
               uri.IsDefaultPort &&
               uri.AbsolutePath.StartsWith('/', StringComparison.Ordinal);
    }
}
