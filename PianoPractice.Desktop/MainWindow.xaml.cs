using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using PianoPractice.Desktop.Models;

namespace PianoPractice.Desktop;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = new();
    private bool _notationReady;
    private bool _sightReadingRendererReady;
    private int _notationZoom = 100;
    private int _displayedScorePage = 1;
    private int _displayedScorePageCount = 1;
    private long _rendererTimelineGeneration;
    private readonly SemaphoreSlim _rendererLifecycleGate = new(1, 1);
    private bool _cursorDispatchActive;
    private double? _pendingCursorBeat;
    private double _lastQueuedCursorBeat;
    private long _rendererFeedbackDispatchId;
    private long _sightReadingFeedbackDispatchId;
    private bool _rendererClosed;
    private readonly HashSet<Key> _pressedComputerPianoKeys = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _notationZoom = _viewModel.NotationZoomPercent;
        ZoomLevelButton.Content = $"{_notationZoom}%";
        PageReadingModeRadio.IsChecked = _viewModel.ReadingMode == ScoreReadingMode.Page;
        ContinuousReadingModeRadio.IsChecked = _viewModel.ReadingMode == ScoreReadingMode.Continuous;
        BothHandsModeRadio.IsChecked = _viewModel.SelectedMode == PracticeMode.BothHands;
        LeftHandModeRadio.IsChecked = _viewModel.SelectedMode == PracticeMode.LeftHand;
        RightHandModeRadio.IsChecked = _viewModel.SelectedMode == PracticeMode.RightHand;
        SyncLessonModeRadios();
        NoPedalRadio.IsChecked = !_viewModel.PedalEnabled;
        YesPedalRadio.IsChecked = _viewModel.PedalEnabled;
        Loaded += MainWindow_Loaded;
        _viewModel.CorrectFeedback += ViewModel_CorrectFeedback;
        _viewModel.NoteFeedback += ViewModel_NoteFeedback;
        _viewModel.PartialChordReset += ViewModel_PartialChordReset;
        _viewModel.CountdownStepRequested += ViewModel_CountdownStepRequested;
        _viewModel.HideCountdownRequested += ViewModel_HideCountdownRequested;
        _viewModel.LessonRunStateChanged += ViewModel_LessonRunStateChanged;
        _viewModel.DisplayPageChangeRequested += ViewModel_DisplayPageChangeRequested;
        _viewModel.ReturnToLivePageRequested += ViewModel_ReturnToLivePageRequested;
        _viewModel.ResultsPresented += ViewModel_ResultsPresented;
        _viewModel.AutoRepeatUpdated += ViewModel_AutoRepeatUpdated;
        _viewModel.ResultsDismissed += ViewModel_ResultsDismissed;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.OnLiveNoteFeedbackTriggered += ViewModel_LiveNoteFeedbackTriggered;
        _viewModel.SightReadingPromptChanged += ViewModel_SightReadingPromptChanged;
        _viewModel.SightReadingFeedback += ViewModel_SightReadingFeedback;
        CompositionTarget.Rendering += CompositionTarget_Rendering;
    }

    private void SyncLessonModeRadios()
    {
        ListenModeRadio.IsChecked = _viewModel.SelectedLessonMode == LessonMode.Listen;
        PracticeModeRadio.IsChecked = _viewModel.SelectedLessonMode == LessonMode.WaitForYou;
        PerformanceModeRadio.IsChecked = _viewModel.SelectedLessonMode == LessonMode.TimedPlay;
    }

    private async void ViewModel_LiveNoteFeedbackTriggered(object? sender, (string kind, double beat, int midiNote) e)
    {
        if (!_notationReady || NotationWebView.CoreWebView2 is null) return;
        var script = $"window.CadenzaNotation.showTemporaryLiveNoteFeedback({JsonSerializer.Serialize(e.kind)}, {e.beat.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {e.midiNote});";
        if (!Dispatcher.CheckAccess())
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                if (NotationWebView.CoreWebView2 is not null)
                {
                    await ExecuteRendererScriptAsync(script);
                }
            });
            return;
        }
        await ExecuteRendererScriptAsync(script);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await InitializeNotationAsync();
        _viewModel.RefreshMidiDevices();
        try
        {
            if (!_viewModel.TryLoadLastOpenedScore())
            {
                _viewModel.SetStatusMessage("Step 1: import a MusicXML score.");
                return;
            }
            await LoadNotationAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Score import failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task InitializeNotationAsync()
    {
        try
        {
            var userDataFolder = Path.Combine(AppStoragePaths.ProductDirectory, "WebView2");
            var options = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions(
                additionalBrowserArguments: "--disable-background-timer-throttling --disable-backgrounding-occluded-windows --autoplay-policy=no-user-gesture-required");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder, options: options);
            await NotationWebView.EnsureCoreWebView2Async(environment);
            await InstallRuntimeHardeningAsync();
            var assetsFolder = Path.Combine(AppContext.BaseDirectory, "Assets", "Verovio");
            NotationWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "cadenza.local",
                assetsFolder,
                CoreWebView2HostResourceAccessKind.DenyCors);
            var playerPath = Path.Combine(assetsFolder, "player.html");
            var rendererVersion = File.GetLastWriteTimeUtc(playerPath).Ticks;
            NotationWebView.Source = new Uri($"https://cadenza.local/player.html?v={rendererVersion}");

            await SightReadingWebView.EnsureCoreWebView2Async(environment);
            ConfigureSightReadingRenderer(SightReadingWebView.CoreWebView2, assetsFolder);
            SightReadingWebView.Source = new Uri($"https://cadenza.local/player.html?v={rendererVersion}&mode=sight-reading");
        }
        catch (Exception exception)
        {
            _viewModel.SetStatusMessage($"The standard notation engine could not start: {exception.Message}");
        }
    }

    private async Task HandleNotationWebMessageAsync(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeProperty) ? typeProperty.GetString() : null;
            if (type == "ready")
            {
                _notationReady = true;
                await LoadNotationAsync();
            }
            else if (type == "rendered")
            {
                ReturnToLivePageButton.Visibility = Visibility.Collapsed;
                var version = root.TryGetProperty("version", out var versionProperty) ? versionProperty.GetString() : "unknown";
                var pages = root.TryGetProperty("pages", out var pagesProperty) ? pagesProperty.GetInt32() : 1;
                _displayedScorePage = 1;
                _displayedScorePageCount = Math.Max(1, pages);
                ScorePageIndicator.Text = $"Page 1 / {pages} · System 1";
                _viewModel.SetStatusMessage($"Score engraved locally with Verovio {version} and SMuFL notation.");
                if (_viewModel.CurrentScore?.PerformanceMeasures is { Count: > 0 } timeline)
                {
                    var json = JsonSerializer.Serialize(timeline.Select(occ => new
                    {
                        occurrenceIndex = occ.OccurrenceIndex,
                        sourceMeasureIndex = occ.SourceMeasureIndex,
                        measureNumber = occ.MeasureNumber,
                        sourceStartBeat = occ.SourceStartBeat,
                        performanceStartBeat = occ.PerformanceStartBeat,
                        durationBeats = occ.DurationBeats,
                        repeatPass = occ.RepeatPass
                    }));
                    await ExecuteRendererScriptAsync($"window.CadenzaNotation.setPerformanceTimeline({json});");
                }
                await ExecuteRendererScriptAsync(
                    $"window.CadenzaNotation.setCursorBeat({_viewModel.CursorBeat.ToString(System.Globalization.CultureInfo.InvariantCulture)});");
                await Task.Delay(240);
                await CaptureRendererStateAsync("loaded");
            }
            else if (type == "error")
            {
                var message = root.TryGetProperty("message", out var messageProperty) ? messageProperty.GetString() : "Unknown engraving error.";
                _viewModel.SetStatusMessage($"Notation import failed: {message}");
            }
            else if (type is "pageChanged" or "position" or "modeChanged")
            {
                var page = root.TryGetProperty("page", out var pageProperty) ? pageProperty.GetInt32() : 1;
                var pages = root.TryGetProperty("pages", out var pagesProperty) ? pagesProperty.GetInt32() : 1;
                var system = root.TryGetProperty("system", out var systemProperty) ? systemProperty.GetInt32() : 1;
                var systems = root.TryGetProperty("systems", out var systemsProperty) ? systemsProperty.GetInt32() : 1;
                _displayedScorePage = page;
                _displayedScorePageCount = Math.Max(1, pages);
                ScorePageIndicator.Text = $"Page {page} / {pages} · System {system} / {systems}";
            }
            else if (type == "manualPageBrowsing")
            {
                var active = root.TryGetProperty("active", out var activeProperty) && activeProperty.GetBoolean();
                ReturnToLivePageButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            }
            else if (type == "repeatLesson")
            {
                _viewModel.DismissResults();
                await _viewModel.RestartPreviewAsync();
            }
            else if (type == "dismissResults")
            {
                _viewModel.DismissResults();
            }
            else if (type is "layoutValidation" or "runtimeTelemetry" or "alignmentTelemetry" or "feedbackTelemetry" or "feedbackAck" or "feedbackReapplied" or "layoutError")
            {
                await SaveRendererDiagnosticAsync(type, root);
            }
        }
        catch (Exception exception)
        {
            _viewModel.SetStatusMessage($"Notation engine message error: {exception.Message}");
        }
    }

    private static async Task SaveRendererDiagnosticAsync(string type, JsonElement payload)
    {
        var diagnosticsFolder = AppStoragePaths.DiagnosticsDirectory;
        Directory.CreateDirectory(diagnosticsFolder);
        var mode = payload.TryGetProperty("mode", out var modeProperty)
            ? modeProperty.GetString()?.ToLowerInvariant()
            : null;
        var suffix = type == "layoutValidation" && !string.IsNullOrWhiteSpace(mode)
            ? $"-{mode}"
            : string.Empty;
        var fileName = $"last-renderer-{type.Replace("Validation", "-validation").ToLowerInvariant()}{suffix}.json";
        await File.WriteAllTextAsync(
            Path.Combine(diagnosticsFolder, fileName),
            payload.GetRawText());
    }

    private async Task CaptureRendererStateAsync(string checkpoint)
    {
        if (!_notationReady || NotationWebView.CoreWebView2 is null) return;
        var scriptResult = await ExecuteRendererScriptAsync(
            "JSON.stringify(window.CadenzaNotation.getState())");
        if (scriptResult is null) return;
        var rendererJson = JsonSerializer.Deserialize<string>(scriptResult);
        if (string.IsNullOrWhiteSpace(rendererJson)) return;
        var diagnosticsFolder = AppStoragePaths.DiagnosticsDirectory;
        Directory.CreateDirectory(diagnosticsFolder);
        await File.WriteAllTextAsync(
            Path.Combine(diagnosticsFolder, $"renderer-state-{checkpoint}.json"),
            rendererJson);
    }

    private async Task LoadNotationAsync()
    {
        if (!_notationReady || _viewModel.CurrentScore is not { } score || NotationWebView.CoreWebView2 is null) return;
        var generation = Interlocked.Increment(ref _rendererTimelineGeneration);
        await _rendererLifecycleGate.WaitAsync();
        try
        {
            if (_rendererClosed || generation != _rendererTimelineGeneration ||
                !ReferenceEquals(score, _viewModel.CurrentScore))
                return;

            var usesValidatedDocument = score.ValidatedMusicXml.Length > 0;
            var bytes = usesValidatedDocument
                ? score.ValidatedMusicXml
                : await File.ReadAllBytesAsync(score.SourcePath);
            if (generation != _rendererTimelineGeneration || !ReferenceEquals(score, _viewModel.CurrentScore))
                return;

            var base64 = Convert.ToBase64String(bytes);
            var zipped = !usesValidatedDocument &&
                         (string.Equals(Path.GetExtension(score.SourcePath), ".mxl", StringComparison.OrdinalIgnoreCase) ||
                          (bytes.Length >= 2 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K'));
            var script = $"window.CadenzaNotation.loadScore({JsonSerializer.Serialize(base64)}, {zipped.ToString().ToLowerInvariant()}, {JsonSerializer.Serialize(_viewModel.ReadingMode.ToString())}, {score.TempoBpm.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {JsonSerializer.Serialize(score.KeySignature)});";
            await ExecuteRendererScriptAsync(script, generation);
            await ExecuteRendererScriptAsync(
                $"window.CadenzaNotation.setZoom({_viewModel.NotationZoomPercent}); window.CadenzaNotation.setHintMode({(_viewModel.HintModeEnabled ? "true" : "false")}); window.CadenzaNotation.setHandMode({JsonSerializer.Serialize(_viewModel.SelectedMode.ToString())}); window.CadenzaNotation.setTempo({_viewModel.EffectiveLessonTempoBpm.ToString(System.Globalization.CultureInfo.InvariantCulture)}); window.CadenzaNotation.setScoreAppearance({_viewModel.CustomScoreScale}, {_viewModel.CustomScoreMargin});",
                generation);
        }
        finally
        {
            _rendererLifecycleGate.Release();
        }
    }

    private async Task<string?> ExecuteRendererScriptAsync(string script, long? expectedGeneration = null)
    {
        if (_rendererClosed ||
            (expectedGeneration.HasValue && expectedGeneration.Value != _rendererTimelineGeneration))
            return null;

        var core = NotationWebView.CoreWebView2;
        if (core is null)
            return null;

        try
        {
            return await core.ExecuteScriptAsync(script);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ObjectDisposedException or COMException)
        {
            // Closing, navigation, and score replacement invalidate pending renderer work.
            return null;
        }
    }

    private void ImportScore_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import MusicXML scores into library",
            Filter = "MusicXML scores|*.mxl;*.musicxml;*.xml|All files|*.*",
            CheckFileExists = true,
            Multiselect = true
        };

        if (dialog.ShowDialog(this) != true) return;
        try
        {
            foreach (var fileName in dialog.FileNames)
            {
                _viewModel.LoadScore(fileName);
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Score import failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PlayLibraryItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: LibraryItemViewModel item })
        {
            _viewModel.LoadLibraryItem(item);
        }
    }

    private void RenameLibraryItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: LibraryItemViewModel item })
        {
            _viewModel.OpenRenameOverlay(item);
        }
    }

    private void DeleteLibraryItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: LibraryItemViewModel item })
        {
            _viewModel.DeleteLibraryItem(item);
        }
    }

    private void DeleteSelectedLibrary_Click(object sender, RoutedEventArgs e) => _viewModel.DeleteSelectedLibraryItems();

    private void ClearLibrarySelection_Click(object sender, RoutedEventArgs e) => _viewModel.ClearLibrarySelection();

    private void LibraryPrevPage_Click(object sender, RoutedEventArgs e) => _viewModel.LibraryPreviousPage();

    private void LibraryNextPage_Click(object sender, RoutedEventArgs e) => _viewModel.LibraryNextPage();

    private void SaveRenameItem_Click(object sender, RoutedEventArgs e) => _viewModel.SaveRenameItem();

    private void CloseRenameOverlay_Click(object sender, RoutedEventArgs e) => _viewModel.CloseRenameOverlay();

    private void ImportMidi_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import a MIDI listen/reference file",
            Filter = "Standard MIDI files|*.mid;*.midi|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _viewModel.LoadMidiReference(dialog.FileName);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "MIDI import failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportPdf_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Attach a PDF score for review",
            Filter = "PDF documents|*.pdf",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _viewModel.LoadPdfReference(dialog.FileName);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "PDF attachment failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void TogglePreview_Click(object sender, RoutedEventArgs e) => await _viewModel.TogglePreviewAsync();

    private async void TransportPrevious_Click(object sender, RoutedEventArgs e) => await _viewModel.SeekPreviewMeasureAsync(-1);

    private async void TransportRestart_Click(object sender, RoutedEventArgs e) => await _viewModel.RestartPreviewAsync();

    private async void TransportNext_Click(object sender, RoutedEventArgs e) => await _viewModel.SeekPreviewMeasureAsync(1);

    private void TransportStop_Click(object sender, RoutedEventArgs e) => _viewModel.StopTransport();

    private async void PlayMidi_Click(object sender, RoutedEventArgs e) => await _viewModel.PlayMidiReferenceAsync();

    private void OpenPdf_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _viewModel.OpenPdfReference();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "PDF viewer failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void CalibrateLatency_Click(object sender, RoutedEventArgs e) => await _viewModel.CalibrateLatencyAsync();

    private async void TestMonitorTone_Click(object sender, RoutedEventArgs e) => await _viewModel.TestMonitorToneAsync();

    private void StopPreview_Click(object sender, RoutedEventArgs e) => _viewModel.StopPreview();

    private void RefreshMidi_Click(object sender, RoutedEventArgs e) => _viewModel.RefreshMidiDevices();

    private void PracticeMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<PracticeMode>(tag, out var mode))
        {
            _viewModel.SetPracticeMode(mode);
            if (_notationReady && NotationWebView.CoreWebView2 is not null)
            {
                _ = ExecuteRendererScriptAsync(
                    $"window.CadenzaNotation.setHandMode({JsonSerializer.Serialize(mode.ToString())});");
            }
        }
    }

    private async void LessonMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<LessonMode>(tag, out var mode))
        {
            if (!await _viewModel.SwitchLessonModeAsync(mode))
                SyncLessonModeRadios();
        }
    }

    private void ReadingMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<ScoreReadingMode>(tag, out var mode))
        {
            _viewModel.SetReadingMode(mode);
            if (_notationReady)
            {
                _ = ExecuteRendererScriptAsync(
                    $"window.CadenzaNotation.setReadingMode({JsonSerializer.Serialize(mode.ToString())});");
            }
        }
    }

    private void HintMode_Click(object sender, RoutedEventArgs e)
    {
        if (!_notationReady || NotationWebView.CoreWebView2 is null || sender is not System.Windows.Controls.CheckBox checkBox) return;
        _viewModel.HintModeEnabled = checkBox.IsChecked == true;
        _ = ExecuteRendererScriptAsync(
            $"window.CadenzaNotation.setHintMode({(checkBox.IsChecked == true ? "true" : "false")});");
    }

    private void OpenLesson_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenCurrentLesson();
        NotationWebView.Visibility = Visibility.Visible;
    }

    private void ConfigureSightReadingRenderer(CoreWebView2 core, string assetsFolder)
    {
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsWebMessageEnabled = true;
        core.SetVirtualHostNameToFolderMapping(
            "cadenza.local",
            assetsFolder,
            CoreWebView2HostResourceAccessKind.DenyCors);
        core.NavigationStarting += SightReadingNavigationStarting;
        core.FrameNavigationStarting += SightReadingNavigationStarting;
        core.NewWindowRequested += SightReadingNewWindowRequested;
        core.PermissionRequested += SightReadingPermissionRequested;
        core.DownloadStarting += SightReadingDownloadStarting;
        core.WebMessageReceived += SightReadingWebMessageReceived;
    }

    private static void SightReadingNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "cadenza.local", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
        }
    }

    private static void SightReadingNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e) =>
        e.Handled = true;

    private static void SightReadingPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        e.State = CoreWebView2PermissionState.Deny;
        e.Handled = true;
    }

    private static void SightReadingDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e) =>
        e.Cancel = true;

    private async void SightReadingWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeProperty) ? typeProperty.GetString() : null;
            if (type == "ready")
            {
                _sightReadingRendererReady = true;
                await LoadSightReadingPromptAsync();
            }
            else if (type == "rendered")
            {
                await ExecuteSightReadingScriptAsync(
                    $"window.CadenzaNotation.beginLesson('WaitForYou', 0, false, {_viewModel.SightReadingSessionGeneration}, false);");
            }
            else if (type == "error")
            {
                var message = root.TryGetProperty("message", out var property)
                    ? property.GetString()
                    : "Unknown sight-reading engraving error.";
                _viewModel.SetSightReadingRendererError(message ?? "Unknown sight-reading engraving error.");
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            _viewModel.SetSightReadingRendererError(exception.Message);
        }
    }

    private async void ViewModel_SightReadingPromptChanged(object? sender, SightReadingPrompt prompt) =>
        await LoadSightReadingPromptAsync();

    private async void ViewModel_SightReadingFeedback(object? sender, SightReadingFeedbackEvent e)
    {
        if (!_sightReadingRendererReady || SightReadingWebView.CoreWebView2 is null) return;
        var dispatchId = Interlocked.Increment(ref _sightReadingFeedbackDispatchId);
        await ExecuteSightReadingScriptAsync(
            $"window.CadenzaNotation.showFeedback({JsonSerializer.Serialize(e.Kind)}, {e.Beat.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {e.MidiNoteNumber}, {dispatchId}, {e.SessionGeneration}, {e.EventId}, 0, {e.StaffNumber});");
    }

    private async Task LoadSightReadingPromptAsync()
    {
        if (!_sightReadingRendererReady ||
            SightReadingWebView.CoreWebView2 is null ||
            _viewModel.CurrentSightReadingPrompt is not { } prompt)
            return;

        var base64 = Convert.ToBase64String(prompt.MusicXml);
        await ExecuteSightReadingScriptAsync(
            $"window.CadenzaNotation.loadScore({JsonSerializer.Serialize(base64)}, false, 'Page', 80, 'C major'); window.CadenzaNotation.setHintMode(false); window.CadenzaNotation.setZoom(115);");
    }

    private async Task<string?> ExecuteSightReadingScriptAsync(string script)
    {
        if (_rendererClosed || SightReadingWebView.CoreWebView2 is not { } core) return null;
        try
        {
            return await core.ExecuteScriptAsync(script);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or COMException)
        {
            return null;
        }
    }

    private void BackToDashboard_Click(object sender, RoutedEventArgs e) => _viewModel.ShowDashboard();

    private void OpenSightReading_Click(object sender, RoutedEventArgs e) => _viewModel.OpenSightReading();

    private void BackFromSightReading_Click(object sender, RoutedEventArgs e) => _viewModel.ShowDashboard();

    private void SightReadingTest_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } &&
            Enum.TryParse<SightReadingTestKind>(tag, out var kind))
        {
            _viewModel.SelectSightReadingTest(kind);
            _viewModel.StartSightReadingTest();
        }
    }

    private void StartSightReading_Click(object sender, RoutedEventArgs e) => _viewModel.StartSightReadingTest();

    private void StopSightReading_Click(object sender, RoutedEventArgs e) => _viewModel.StopSightReadingTest();

    private void OpenDeviceSettings_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SetMidiShortcutCommandsSuspended(true);
        AudioMixerOverlay.Visibility = Visibility.Collapsed;
        ImportWarningsOverlay.Visibility = Visibility.Collapsed;
        NotationWebView.Visibility = Visibility.Collapsed;
        DeviceSettingsOverlay.Visibility = Visibility.Visible;
    }

    private async void CloseDeviceSettings_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.CancelMidiShortcutLearning();
        DeviceSettingsOverlay.Visibility = Visibility.Collapsed;
        _viewModel.SetMidiShortcutCommandsSuspended(false);
        if (_viewModel.IsPlayerVisible)
        {
            NotationWebView.Visibility = Visibility.Visible;
            if (_notationReady && NotationWebView.CoreWebView2 is not null)
            {
                await ExecuteRendererScriptAsync(
                    $"window.CadenzaNotation.setScoreAppearance({_viewModel.CustomScoreScale}, {_viewModel.CustomScoreMargin});");
            }
        }
    }

    private void SettingsTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out var tabIndex))
        {
            _viewModel.SelectedSettingsTabIndex = tabIndex;
        }
    }

    private void RebindMidiShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string actionId })
        {
            const string keyboardPrefix = "Keyboard:";
            if (actionId.StartsWith(keyboardPrefix, StringComparison.Ordinal))
                _viewModel.StartKeyboardShortcutLearning(actionId[keyboardPrefix.Length..]);
            else
                _viewModel.StartMidiShortcutLearning(actionId);
        }
    }

    private void MapMidiController_Click(object sender, RoutedEventArgs e) =>
        _viewModel.StartMidiControllerAutoMapping();

    private void CancelMidiShortcutLearning_Click(object sender, RoutedEventArgs e) =>
        _viewModel.CancelMidiShortcutLearning();

    private void OpenAudioMixer_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.CancelMidiShortcutLearning();
        _viewModel.SetMidiShortcutCommandsSuspended(true);
        DeviceSettingsOverlay.Visibility = Visibility.Collapsed;
        ImportWarningsOverlay.Visibility = Visibility.Collapsed;
        NotationWebView.Visibility = Visibility.Collapsed;
        AudioMixerOverlay.Visibility = Visibility.Visible;
    }

    private void CloseAudioMixer_Click(object sender, RoutedEventArgs e)
    {
        AudioMixerOverlay.Visibility = Visibility.Collapsed;
        _viewModel.SetMidiShortcutCommandsSuspended(false);
        NotationWebView.Visibility = Visibility.Visible;
    }

    private void OpenImportWarnings_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.CancelMidiShortcutLearning();
        _viewModel.SetMidiShortcutCommandsSuspended(true);
        DeviceSettingsOverlay.Visibility = Visibility.Collapsed;
        AudioMixerOverlay.Visibility = Visibility.Collapsed;
        NotationWebView.Visibility = Visibility.Collapsed;
        ImportWarningsOverlay.Visibility = Visibility.Visible;
    }

    private void CloseImportWarnings_Click(object sender, RoutedEventArgs e)
    {
        ImportWarningsOverlay.Visibility = Visibility.Collapsed;
        _viewModel.SetMidiShortcutCommandsSuspended(false);
        NotationWebView.Visibility = Visibility.Visible;
    }

    private async void ZoomOut_Click(object sender, RoutedEventArgs e) => await SetNotationZoomAsync(_notationZoom - 10);

    private async void ZoomIn_Click(object sender, RoutedEventArgs e) => await SetNotationZoomAsync(_notationZoom + 10);

    private async void ZoomReset_Click(object sender, RoutedEventArgs e) => await SetNotationZoomAsync(100);

    private async void PreviousScorePage_Click(object sender, RoutedEventArgs e)
    {
        await ChangeDisplayedScorePageAsync(-1);
    }

    private async void NextScorePage_Click(object sender, RoutedEventArgs e)
    {
        await ChangeDisplayedScorePageAsync(1);
    }

    private async void ViewModel_DisplayPageChangeRequested(object? sender, int delta) =>
        await ChangeDisplayedScorePageAsync(delta);

    private async void ViewModel_ReturnToLivePageRequested(object? sender, EventArgs e) =>
        await ReturnToLivePageAsync();

    private async Task ChangeDisplayedScorePageAsync(int delta)
    {
        if (delta == 0) return;
        if (_viewModel.ReadingMode == ScoreReadingMode.Continuous)
        {
            await _viewModel.SeekDisplayPageAsync(delta);
            return;
        }

        var targetPage = Math.Clamp(_displayedScorePage + Math.Sign(delta), 1, _displayedScorePageCount);
        if (targetPage == _displayedScorePage) return;

        _viewModel.PausePerformanceForPageNavigation();
        if (_notationReady && NotationWebView.CoreWebView2 is not null)
            await ExecuteRendererScriptAsync($"window.CadenzaNotation.changePage({Math.Sign(delta)});");
    }

    private async void ReturnToLivePage_Click(object sender, RoutedEventArgs e) =>
        await ReturnToLivePageAsync();

    private async Task ReturnToLivePageAsync()
    {
        if (_notationReady && NotationWebView.CoreWebView2 is not null)
            await ExecuteRendererScriptAsync("window.CadenzaNotation.returnToLivePage();");
    }

    private async Task SetNotationZoomAsync(int percent)
    {
        _notationZoom = Math.Clamp(percent, 80, 165);
        _viewModel.NotationZoomPercent = _notationZoom;
        ZoomLevelButton.Content = $"{_notationZoom}%";
        if (_notationReady && NotationWebView.CoreWebView2 is not null)
        {
            await ExecuteRendererScriptAsync(
                $"window.CadenzaNotation.setZoom({_notationZoom});");
        }
    }

    private void PedalSetup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && bool.TryParse(tag, out var enabled))
        {
            _viewModel.SetPedalEnabled(enabled);
        }
    }

    private async void StartLesson_Click(object sender, RoutedEventArgs e) => await _viewModel.StartSelectedModeAsync();

    private void StopLesson_Click(object sender, RoutedEventArgs e) => _viewModel.StopLesson();

    private void FocusInput_Click(object sender, RoutedEventArgs e)
    {
        MidiDeviceComboBox.Focus();
        MidiDeviceComboBox.IsDropDownOpen = true;
    }

    private static bool IsTextInputFocused(KeyEventArgs e)
    {
        var focused = Keyboard.FocusedElement;
        return focused is System.Windows.Controls.Primitives.TextBoxBase ||
               focused is System.Windows.Controls.PasswordBox ||
               e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase ||
               e.OriginalSource is System.Windows.Controls.PasswordBox;
    }

    private bool IsModalOverlayVisible() =>
        ImportWarningsOverlay.Visibility == Visibility.Visible ||
        AudioMixerOverlay.Visibility == Visibility.Visible ||
        DeviceSettingsOverlay.Visibility == Visibility.Visible ||
        RenameLibraryItemOverlay.Visibility == Visibility.Visible;

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsTextInputFocused(e)) return;
        var pressedKey = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_viewModel.IsKeyboardShortcutLearning)
        {
            if (pressedKey == Key.Back)
                _viewModel.UnbindCurrentMidiShortcutLearning();
            else
                _viewModel.TryAssignKeyboardShortcut(pressedKey, Keyboard.Modifiers);
            e.Handled = true;
            return;
        }
        if (_viewModel.IsMidiShortcutLearning && pressedKey == Key.Escape)
        {
            _viewModel.CancelMidiShortcutLearning();
            e.Handled = true;
            return;
        }
        if (_viewModel.IsMidiShortcutLearning && pressedKey == Key.Back)
        {
            if (_viewModel.UnbindCurrentMidiShortcutLearning()) e.Handled = true;
            return;
        }
        if (!_viewModel.IsMusicalWorkspaceVisible) return;
        if (IsModalOverlayVisible()) return;

        // Preserve normal keyboard activation for a focused WPF button.
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.ButtonBase &&
            e.Key is Key.Space or Key.Enter)
            return;

        if (_viewModel.IsPlayerVisible)
        {
            var shortcut = AppShortcutRouter.Resolve(
                pressedKey,
                Keyboard.Modifiers,
                _viewModel.ResultsVisible,
                _viewModel.KeyboardShortcutOverrides);
            if (shortcut != AppShortcutAction.None)
            {
                e.Handled = true;
                if (!e.IsRepeat) await ExecuteAppShortcutAsync(shortcut);
                return;
            }
        }

        if (!e.IsRepeat &&
            _viewModel.UseKeyboardSimulation &&
            AppShortcutRouter.AllowsComputerPianoInput(Keyboard.Modifiers) &&
            ComputerKeyboardPianoMap.MidiNotes.TryGetValue(pressedKey, out var midiNote) &&
            _pressedComputerPianoKeys.Add(pressedKey))
        {
            _viewModel.SimulateNoteOn(midiNote);
            e.Handled = true;
        }
    }

    private async Task ExecuteAppShortcutAsync(AppShortcutAction shortcut)
    {
        switch (shortcut)
        {
            case AppShortcutAction.SelectListen:
                await _viewModel.SwitchLessonModeAsync(LessonMode.Listen);
                break;
            case AppShortcutAction.SelectPractice:
                await _viewModel.SwitchLessonModeAsync(LessonMode.WaitForYou);
                break;
            case AppShortcutAction.SelectPerformance:
                await _viewModel.SwitchLessonModeAsync(LessonMode.TimedPlay);
                break;
            case AppShortcutAction.TogglePlayback:
                if (_viewModel.IsPerformancePaused) await _viewModel.ResumePerformanceAsync();
                else if (_viewModel.IsPracticePaused) _viewModel.ResumePractice();
                else if (_viewModel.IsLessonActive && _viewModel.SelectedLessonMode == LessonMode.TimedPlay)
                    _viewModel.PausePerformanceForPageNavigation(forPageNavigation: false);
                else if (_viewModel.IsLessonActive && _viewModel.SelectedLessonMode == LessonMode.WaitForYou)
                    _viewModel.PausePractice();
                else if (_viewModel.IsLessonActive) _viewModel.StopLesson();
                else await _viewModel.StartSelectedModeAsync();
                break;
            case AppShortcutAction.Stop:
                _viewModel.StopTransport();
                break;
            case AppShortcutAction.Restart:
                await _viewModel.RestartPreviewAsync();
                break;
            case AppShortcutAction.PreviousMeasure:
                await _viewModel.SeekDisplayMeasureAsync(-1);
                break;
            case AppShortcutAction.NextMeasure:
                await _viewModel.SeekDisplayMeasureAsync(1);
                break;
            case AppShortcutAction.PreviousPage:
                await ChangeDisplayedScorePageAsync(-1);
                break;
            case AppShortcutAction.NextPage:
                await ChangeDisplayedScorePageAsync(1);
                break;
            case AppShortcutAction.ReturnToLivePage:
                await ReturnToLivePageAsync();
                break;
            case AppShortcutAction.ToggleLoop:
                _viewModel.IsLoopEnabled = !_viewModel.IsLoopEnabled;
                break;
            case AppShortcutAction.DismissResults:
                _viewModel.DismissResults();
                break;
            case AppShortcutAction.RepeatResults:
                await _viewModel.TriggerAutoRepeatAsync();
                break;
        }
    }

    private void TempoField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not System.Windows.Controls.TextBox textBox) return;
        textBox.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (_pressedComputerPianoKeys.Remove(e.Key) &&
            ComputerKeyboardPianoMap.MidiNotes.TryGetValue(e.Key, out var midiNote))
        {
            _viewModel.SimulateNoteOff(midiNote);
            e.Handled = true;
        }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        foreach (var key in _pressedComputerPianoKeys.ToArray())
        {
            if (ComputerKeyboardPianoMap.MidiNotes.TryGetValue(key, out var midiNote))
                _viewModel.SimulateNoteOff(midiNote);
        }

        _pressedComputerPianoKeys.Clear();
    }

    private async void RepeatLesson_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.DismissResults();
        await _viewModel.RestartPreviewAsync();
    }

    private void DismissResults_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.DismissResults();
        NotationWebView.Visibility = Visibility.Visible;
    }

    private void CompositionTarget_Rendering(object? sender, EventArgs e) => _viewModel.UpdateVisualClock();

    private void ViewModel_CorrectFeedback(object? sender, EventArgs e)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        LiveStatsPanel.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(.55, 1, TimeSpan.FromMilliseconds(240))
            {
                AutoReverse = true
            });
    }

    private void PulseMidiBadge()
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        MidiPulseBadge.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(.35, 1, TimeSpan.FromMilliseconds(230))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private async void ViewModel_NoteFeedback(object? sender, LessonNoteFeedbackEvent e)
    {
        if (!_notationReady || NotationWebView.CoreWebView2 is null) return;
        await _rendererLifecycleGate.WaitAsync();
        try
        {
            var dispatchId = Interlocked.Increment(ref _rendererFeedbackDispatchId);
            await ExecuteRendererScriptAsync(
                $"window.CadenzaNotation.showFeedback({JsonSerializer.Serialize(e.Kind)}, {e.Beat.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {(e.MidiNoteNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null")}, {dispatchId}, {e.RunGeneration}, {e.EventId}, {e.OccurrenceIndex}, {e.StaffNumber});");
        }
        finally
        {
            _rendererLifecycleGate.Release();
        }
    }

    private async void ViewModel_PartialChordReset(object? sender, int occurrenceIndex)
    {
        if (!_notationReady || NotationWebView.CoreWebView2 is null) return;
        var beat = _viewModel.CursorBeat.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await _rendererLifecycleGate.WaitAsync();
        try
        {
            await ExecuteRendererScriptAsync(
                $"window.CadenzaNotation.clearPartialFeedback({occurrenceIndex}, {beat});");
        }
        finally
        {
            _rendererLifecycleGate.Release();
        }
    }

    private async void ViewModel_CountdownStepRequested(object? sender, string stepText)
    {
        if (!_notationReady || NotationWebView.CoreWebView2 is null) return;
        var script = $"window.CadenzaNotation.showCountdownStep({JsonSerializer.Serialize(stepText)});";
        if (!Dispatcher.CheckAccess())
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                if (NotationWebView.CoreWebView2 is not null)
                {
                    await ExecuteRendererScriptAsync(script);
                }
            });
            return;
        }
        await ExecuteRendererScriptAsync(script);
    }

    private async void ViewModel_HideCountdownRequested(object? sender, EventArgs e)
    {
        if (!_notationReady || NotationWebView.CoreWebView2 is null) return;
        var script = "window.CadenzaNotation.hideCountdown();";
        if (!Dispatcher.CheckAccess())
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                if (NotationWebView.CoreWebView2 is not null)
                {
                    await ExecuteRendererScriptAsync(script);
                }
            });
            return;
        }
        await ExecuteRendererScriptAsync(script);
    }

    private async void ViewModel_LessonRunStateChanged(object? sender, LessonRunStateEvent e)
    {
        if (!_notationReady || NotationWebView.CoreWebView2 is null) return;
        await _rendererLifecycleGate.WaitAsync();
        try
        {
            _rendererTimelineGeneration++;
            _pendingCursorBeat = null;
            _lastQueuedCursorBeat = e.StartBeat;
            var startBeat = e.StartBeat.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var script = e.State switch
            {
                "started" =>
                    $"window.CadenzaNotation.beginLesson({JsonSerializer.Serialize(e.Mode.ToString())}, {startBeat}, false, {e.RunGeneration}, {_viewModel.OnlyShowFeedbackOnPerformanceEnd.ToString().ToLowerInvariant()});",
                "paused" => "window.CadenzaNotation.endTimeline();",
                "resume-countdown" => "window.CadenzaNotation.returnToLivePage(false);",
                "resumed" => $"window.CadenzaNotation.beginTimeline({startBeat});",
                _ => $"window.CadenzaNotation.finishLesson({(e.State == "completed" ? "true" : "false")}, {e.RunGeneration});"
            };
            await ExecuteRendererScriptAsync(script);
            if (e.State is "started" or "resumed")
                QueueRendererCursor(e.StartBeat, allowReset: true);
            else if (e.State is "completed" or "stopped")
                await CaptureRendererStateAsync($"lesson-{e.State}");
        }
        finally
        {
            _rendererLifecycleGate.Release();
        }
    }

    private async void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.LastMidiKeyLabel))
        {
            PulseMidiBadge();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsPlayerVisible))
        {
            if (_viewModel.IsPlayerVisible)
            {
                NotationWebView.Visibility = Visibility.Visible;
                await LoadNotationAsync();
            }
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.SelectedLessonMode))
        {
            SyncLessonModeRadios();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.CurrentScore))
        {
            if (_viewModel.IsPlayerVisible)
                await LoadNotationAsync();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.NotationZoomPercent))
        {
            _notationZoom = _viewModel.NotationZoomPercent;
            ZoomLevelButton.Content = $"{_notationZoom}%";
            if (_notationReady && NotationWebView.CoreWebView2 is not null)
            {
                await ExecuteRendererScriptAsync($"window.CadenzaNotation.setZoom({_notationZoom});");
            }
        }
        else if (e.PropertyName is nameof(MainWindowViewModel.CustomScoreScale) or nameof(MainWindowViewModel.CustomScoreMargin) or nameof(MainWindowViewModel.CustomNoteSpacing) or nameof(MainWindowViewModel.CustomBarDensity))
        {
            if (_notationReady && NotationWebView.CoreWebView2 is not null)
            {
                await ExecuteRendererScriptAsync(
                    $"window.CadenzaNotation.setScoreAppearance({_viewModel.CustomScoreScale}, {_viewModel.CustomScoreMargin}, {_viewModel.CustomNoteSpacing}, {_viewModel.CustomBarDensity});");
            }
        }

        if (!_notationReady || NotationWebView.CoreWebView2 is null) return;
        if (e.PropertyName == nameof(MainWindowViewModel.IsPreviewPlaying))
        {
            // Capture the transition before awaiting the renderer. During a restart,
            // audio and the corrected clock can advance CursorBeat while the prior
            // timeline is still finishing, which would otherwise skip the opening beat.
            var previewIsPlaying = _viewModel.IsScorePreviewPlaying;
            var previewStartBeat = _viewModel.CursorBeat;
            ResetCorrectedPerformanceClock();
            await _rendererLifecycleGate.WaitAsync();
            try
            {
                if (previewIsPlaying)
                {
                    _rendererTimelineGeneration++;
                    _lastQueuedCursorBeat = previewStartBeat;
                    await ExecuteRendererScriptAsync(
                        $"window.CadenzaNotation.beginTimeline({previewStartBeat.ToString(System.Globalization.CultureInfo.InvariantCulture)});");
                    QueueRendererCursor(previewStartBeat, allowReset: true);
                }
                else
                {
                    await ExecuteRendererScriptAsync("window.CadenzaNotation.endTimeline();");
                    await CaptureRendererStateAsync("timeline-ended");
                }
            }
            finally
            {
                _rendererLifecycleGate.Release();
            }
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsLessonActive))
        {
            await ExecuteRendererScriptAsync(
                $"window.CadenzaNotation.setTempo({_viewModel.EffectiveLessonTempoBpm.ToString(System.Globalization.CultureInfo.InvariantCulture)});");
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.CursorBeat))
        {
            QueueRendererCursor(_viewModel.CursorBeat);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.LessonTempoPercent))
        {
            await ExecuteRendererScriptAsync(
                $"window.CadenzaNotation.setTempo({_viewModel.EffectiveLessonTempoBpm.ToString(System.Globalization.CultureInfo.InvariantCulture)});");
        }
    }

    private void QueueRendererCursor(double beat, bool allowReset = false)
    {
        if (!_notationReady || NotationWebView.CoreWebView2 is null) return;
        var running = _viewModel.IsLessonActive || _viewModel.IsScorePreviewPlaying;
        if (!allowReset && running && beat + 0.0001 < _lastQueuedCursorBeat) return;
        if (allowReset || (!running && beat + 0.0001 < _lastQueuedCursorBeat))
        {
            _rendererTimelineGeneration++;
        }

        _lastQueuedCursorBeat = beat;
        _pendingCursorBeat = beat;
        if (!_cursorDispatchActive) _ = DrainRendererCursorAsync();
    }

    private async Task DrainRendererCursorAsync()
    {
        if (_cursorDispatchActive) return;
        _cursorDispatchActive = true;
        try
        {
            while (_pendingCursorBeat is { } beat && _notationReady && NotationWebView.CoreWebView2 is not null)
            {
                var generation = _rendererTimelineGeneration;
                _pendingCursorBeat = null;
                await ExecuteRendererScriptAsync(
                    $"window.CadenzaNotation.setCursorBeat({beat.ToString(System.Globalization.CultureInfo.InvariantCulture)});");
                if (generation != _rendererTimelineGeneration) continue;
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            _cursorDispatchActive = false;
            if (_pendingCursorBeat is not null && _notationReady) _ = DrainRendererCursorAsync();
        }
    }

    private async void ViewModel_ResultsPresented(object? sender, EventArgs e)
    {
        if (!_notationReady || NotationWebView.CoreWebView2 is null) return;
        await _rendererLifecycleGate.WaitAsync();
        try
        {
            var pedalJson = _viewModel.PedalStatValue is { } p ? JsonSerializer.Serialize(p) : "null";
            await ExecuteRendererScriptAsync(
                $"window.CadenzaNotation.showResultsModal(" +
                $"{JsonSerializer.Serialize(_viewModel.ResultHeadline)}, " +
                $"{JsonSerializer.Serialize(_viewModel.RewardLabel)}, " +
                $"{JsonSerializer.Serialize(_viewModel.AccuracyLabel)}, " +
                $"{JsonSerializer.Serialize(_viewModel.TimingStatValue)}, " +
                $"{JsonSerializer.Serialize(_viewModel.ResultElapsedTimeLabel)}, " +
                $"{JsonSerializer.Serialize(_viewModel.ResultTargetTimeLabel)}, " +
                $"{JsonSerializer.Serialize(_viewModel.HoldStatValue)}, " +
                $"{pedalJson}, " +
                $"{_viewModel.CorrectCount}, " +
                $"{_viewModel.MissedCount}, " +
                $"{_viewModel.ExtraCount}, " +
                $"{JsonSerializer.Serialize(_viewModel.AutoRepeatStatusText)}, " +
                $"{_viewModel.AutoRepeatProgress.ToString(System.Globalization.CultureInfo.InvariantCulture)}, " +
                $"{JsonSerializer.Serialize(_viewModel.VoicingStatValue)}, " +
                $"{JsonSerializer.Serialize(_viewModel.ArticulationStatValue)}, " +
                $"{JsonSerializer.Serialize(_viewModel.ChordSyncStatValue)});");
        }
        finally
        {
            _rendererLifecycleGate.Release();
        }
    }

    private async void ViewModel_AutoRepeatUpdated(object? sender, EventArgs e)
    {
        if (!_notationReady || NotationWebView.CoreWebView2 is null || !_viewModel.ResultsVisible) return;
        await _rendererLifecycleGate.WaitAsync();
        try
        {
            await ExecuteRendererScriptAsync(
                $"window.CadenzaNotation.updateResultsAutoRepeat(" +
                $"{JsonSerializer.Serialize(_viewModel.AutoRepeatStatusText)}, " +
                $"{_viewModel.AutoRepeatProgress.ToString(System.Globalization.CultureInfo.InvariantCulture)});");
        }
        finally
        {
            _rendererLifecycleGate.Release();
        }
    }

    private async void ViewModel_ResultsDismissed(object? sender, EventArgs e)
    {
        if (!_notationReady || NotationWebView.CoreWebView2 is null) return;
        await _rendererLifecycleGate.WaitAsync();
        try
        {
            await ExecuteRendererScriptAsync("window.CadenzaNotation.hideResultsModal();");
        }
        finally
        {
            _rendererLifecycleGate.Release();
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _rendererClosed = true;
        Interlocked.Increment(ref _rendererTimelineGeneration);
        _viewModel.CorrectFeedback -= ViewModel_CorrectFeedback;
        _viewModel.NoteFeedback -= ViewModel_NoteFeedback;
        _viewModel.LessonRunStateChanged -= ViewModel_LessonRunStateChanged;
        _viewModel.DisplayPageChangeRequested -= ViewModel_DisplayPageChangeRequested;
        _viewModel.ReturnToLivePageRequested -= ViewModel_ReturnToLivePageRequested;
        _viewModel.ResultsPresented -= ViewModel_ResultsPresented;
        _viewModel.AutoRepeatUpdated -= ViewModel_AutoRepeatUpdated;
        _viewModel.ResultsDismissed -= ViewModel_ResultsDismissed;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.SightReadingPromptChanged -= ViewModel_SightReadingPromptChanged;
        _viewModel.SightReadingFeedback -= ViewModel_SightReadingFeedback;
        CompositionTarget.Rendering -= CompositionTarget_Rendering;
        if (NotationWebView.CoreWebView2 is not null)
        {
            RemoveRuntimeHardeningHandlers(NotationWebView.CoreWebView2);
        }
        if (SightReadingWebView.CoreWebView2 is { } sightReadingCore)
        {
            sightReadingCore.NavigationStarting -= SightReadingNavigationStarting;
            sightReadingCore.FrameNavigationStarting -= SightReadingNavigationStarting;
            sightReadingCore.NewWindowRequested -= SightReadingNewWindowRequested;
            sightReadingCore.PermissionRequested -= SightReadingPermissionRequested;
            sightReadingCore.DownloadStarting -= SightReadingDownloadStarting;
            sightReadingCore.WebMessageReceived -= SightReadingWebMessageReceived;
        }
        _viewModel.Dispose();
    }
}
