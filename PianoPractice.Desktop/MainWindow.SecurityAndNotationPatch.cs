using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace PianoPractice.Desktop;

public partial class MainWindow
{
    private const string TrustedNotationOrigin = "https://cadenza.local";
    private bool _runtimeHardeningInstalled;
    private string? _runtimePatchScript;

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
        core.WebMessageReceived += HardenedWebMessageReceived;

        var patchPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Verovio",
            "cadenza-runtime-patch.js");
        if (!File.Exists(patchPath))
        {
            _viewModel.SetStatusMessage("The notation safety patch is missing from the application output.");
            return;
        }

        _runtimePatchScript = await File.ReadAllTextAsync(patchPath);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(_runtimePatchScript);
        await ApplyRuntimePatchToCurrentDocumentAsync();
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

    private async void HardenedWebMessageReceived(
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
            // The existing message handler reports malformed trusted messages.
        }
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
