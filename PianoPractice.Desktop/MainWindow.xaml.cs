using System.IO;
using System.ComponentModel;
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
    private int _notationZoom = 100;
    private long _rendererTimelineGeneration;
    private readonly SemaphoreSlim _rendererLifecycleGate = new(1, 1);
    private bool _cursorDispatchActive;
    private double? _pendingCursorBeat;
    private double _lastQueuedCursorBeat;
    private long _rendererFeedbackDispatchId;

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
        ListenModeRadio.IsChecked = _viewModel.SelectedLessonMode == LessonMode.Listen;
        PracticeModeRadio.IsChecked = _viewModel.SelectedLessonMode == LessonMode.WaitForYou;
        PerformanceModeRadio.IsChecked = _viewModel.SelectedLessonMode == LessonMode.TimedPlay;
        NoPedalRadio.IsChecked = !_viewModel.PedalEnabled;
        YesPedalRadio.IsChecked = _viewModel.PedalEnabled;
        Loaded += MainWindow_Loaded;
        _viewModel.CorrectFeedback += ViewModel_CorrectFeedback;
        _viewModel.NoteFeedback += ViewModel_NoteFeedback;
        _viewModel.PartialChordReset += ViewModel_PartialChordReset;
        _viewModel.CountdownStepRequested += ViewModel_CountdownStepRequested;
        _viewModel.HideCountdownRequested += ViewModel_HideCountdownRequested;
        _viewModel.LessonRunStateChanged += ViewModel_LessonRunStateChanged;
        _viewModel.ResultsPresented += ViewModel_ResultsPresented;
        _viewModel.AutoRepeatUpdated += ViewModel_AutoRepeatUpdated;
        _viewModel.ResultsDismissed += ViewModel_ResultsDismissed;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.OnLiveNoteFeedbackTriggered += ViewModel_LiveNoteFeedbackTriggered;
        _viewModel.OnHoldProgressUpdated += ViewModel_HoldProgressUpdated;
        _viewModel.OnHoldProgressCancelled += ViewModel_HoldProgressCancelled;
        CompositionTarget.Rendering += CompositionTarget_Rendering;
    }

    private async void ViewModel_HoldProgressUpdated(object? sender, (string actionText, string timeText, double progress) e)
    {
        if (!_notationReady || NotationWebView.CoreWebView2 is null) return;
        var script = $"if (window.CadenzaNotation?.showHoldProgress) window.CadenzaNotation.showHoldProgress({JsonSerializer.Serialize(e.actionText)}, {JsonSerializer.Serialize(e.timeText)}, {e.progress.ToString(System.Globalization.CultureInfo.InvariantCulture)});";
        if (!Dispatcher.CheckAccess())
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                if (NotationWebView.CoreWebView2 is not null)
                {
                    await NotationWebView.CoreWebView2.ExecuteScriptAsync(script);
                }
            });
            return;
        }
        await NotationWebView.CoreWebView2.ExecuteScriptAsync(script);
    }

    private async void ViewModel_HoldProgressCancelled(object? sender, EventArgs e)
    {
        if (!_notationReady || NotationWebView.CoreWebView2 is null) return;
        var script = "if (window.CadenzaNotation?.hideHoldProgress) window.CadenzaNotation.hideHoldProgress();";
        if (!Dispatcher.CheckAccess())
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                if (NotationWebView.CoreWebView2 is not null)
                {
                    await NotationWebView.CoreWebView2.ExecuteScriptAsync(script);
                }
            });
            return;
        }
        await NotationWebView.CoreWebView2.ExecuteScriptAsync(script);
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
                    await NotationWebView.CoreWebView2.ExecuteScriptAsync(script);
                }
            });
            return;
        }
        await NotationWebView.CoreWebView2.ExecuteScriptAsync(script);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await InitializeNotationAsync();
        _viewModel.RefreshMidiDevices();
        var defaultScore = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "olivia-rodrigo-drivers-license.mxl");
        if (!File.Exists(defaultScore))
        {
            _viewModel.SetStatusMessage("Step 1: import a MusicXML score.");
            return;
        }

        try
        {
            _viewModel.LoadScore(defaultScore);
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
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CadenzaPianoStudio",
                "WebView2");
            var options = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions(
                additionalBrowserArguments: "--disable-background-timer-throttling --disable-backgrounding-occluded-windows --autoplay-policy=no-user-gesture-required");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder, options: options);
            await NotationWebView.EnsureCoreWebView2Async(environment);
            var assetsFolder = Path.Combine(AppContext.BaseDirectory, "Assets", "Verovio");
            NotationWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "cadenza.local",
                assetsFolder,
                CoreWebView2HostResourceAccessKind.DenyCors);
            NotationWebView.CoreWebView2.WebMessageReceived += NotationWebView_WebMessageReceived;
            var playerPath = Path.Combine(assetsFolder, "player.html");
            var rendererVersion = File.GetLastWriteTimeUtc(playerPath).Ticks;
            NotationWebView.Source = new Uri($"https://cadenza.local/player.html?v={rendererVersion}");
        }
        catch (Exception exception)
        {
            _viewModel.SetStatusMessage($"The standard notation engine could not start: {exception.Message}");
        }
    }

    private async void NotationWebView_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
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
                var version = root.TryGetProperty("version", out var versionProperty) ? versionProperty.GetString() : "unknown";
                var pages = root.TryGetProperty("pages", out var pagesProperty) ? pagesProperty.GetInt32() : 1;
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
                    await NotationWebView.CoreWebView2.ExecuteScriptAsync($"window.CadenzaNotation.setPerformanceTimeline({json});");
                }
                await NotationWebView.CoreWebView2.ExecuteScriptAsync(
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
                ScorePageIndicator.Text = $"Page {page} / {pages} · System {system} / {systems}";
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
        var diagnosticsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cadenza",
            "Diagnostics");
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
        var scriptResult = await NotationWebView.CoreWebView2.ExecuteScriptAsync(
            "JSON.stringify(window.CadenzaNotation.getState())");
        var rendererJson = JsonSerializer.Deserialize<string>(scriptResult);
        if (string.IsNullOrWhiteSpace(rendererJson)) return;
        var diagnosticsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cadenza",
            "Diagnostics");
        Directory.CreateDirectory(diagnosticsFolder);
        await File.WriteAllTextAsync(
            Path.Combine(diagnosticsFolder, $"renderer-state-{checkpoint}.json"),
            rendererJson);
    }

    private async Task LoadNotationAsync()
    {
        if (!_notationReady || _viewModel.CurrentScore is not { } score || NotationWebView.CoreWebView2 is null) return;
        var bytes = await File.ReadAllBytesAsync(score.SourcePath);
        var base64 = Convert.ToBase64String(bytes);
        var zipped = string.Equals(Path.GetExtension(score.SourcePath), ".mxl", StringComparison.OrdinalIgnoreCase) ||
                     (bytes.Length >= 2 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K');
        var script = $"window.CadenzaNotation.loadScore({JsonSerializer.Serialize(base64)}, {zipped.ToString().ToLowerInvariant()}, {JsonSerializer.Serialize(_viewModel.ReadingMode.ToString())}, {score.TempoBpm.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {JsonSerializer.Serialize(score.KeySignature)});";
        await NotationWebView.CoreWebView2.ExecuteScriptAsync(script);
        await NotationWebView.CoreWebView2.ExecuteScriptAsync(
            $"window.CadenzaNotation.setZoom({_viewModel.NotationZoomPercent}); window.CadenzaNotation.setHintMode({(_viewModel.HintModeEnabled ? "true" : "false")}); window.CadenzaNotation.setHandMode({JsonSerializer.Serialize(_viewModel.SelectedMode.ToString())}); window.CadenzaNotation.setTempo({_viewModel.EffectiveLessonTempoBpm.ToString(System.Globalization.CultureInfo.InvariantCulture)}); window.CadenzaNotation.setScoreAppearance({_viewModel.CustomScoreScale}, {_viewModel.CustomScoreMargin});");
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
            _ = LoadNotationAsync();
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
            _ = LoadNotationAsync();
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
                _ = NotationWebView.CoreWebView2.ExecuteScriptAsync(
                    $"window.CadenzaNotation.setHandMode({JsonSerializer.Serialize(mode.ToString())});");
            }
        }
    }

    private async void LessonMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<LessonMode>(tag, out var mode))
        {
            await _viewModel.SwitchLessonModeAsync(mode);
        }
    }

    private void ReadingMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<ScoreReadingMode>(tag, out var mode))
        {
            _viewModel.SetReadingMode(mode);
            if (_notationReady)
            {
                _ = NotationWebView.CoreWebView2.ExecuteScriptAsync(
                    $"window.CadenzaNotation.setReadingMode({JsonSerializer.Serialize(mode.ToString())});");
            }
        }
    }

    private void HintMode_Click(object sender, RoutedEventArgs e)
    {
        if (!_notationReady || NotationWebView.CoreWebView2 is null || sender is not System.Windows.Controls.CheckBox checkBox) return;
        _viewModel.HintModeEnabled = checkBox.IsChecked == true;
        _ = NotationWebView.CoreWebView2.ExecuteScriptAsync(
            $"window.CadenzaNotation.setHintMode({(checkBox.IsChecked == true ? "true" : "false")});");
    }

    private void OpenLesson_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenCurrentLesson();
        NotationWebView.Visibility = Visibility.Visible;
        _ = LoadNotationAsync();
    }

    private void BackToDashboard_Click(object sender, RoutedEventArgs e) => _viewModel.ShowDashboard();

    private void OpenDeviceSettings_Click(object sender, RoutedEventArgs e)
    {
        AudioMixerOverlay.Visibility = Visibility.Collapsed;
        ImportWarningsOverlay.Visibility = Visibility.Collapsed;
        NotationWebView.Visibility = Visibility.Collapsed;
        DeviceSettingsOverlay.Visibility = Visibility.Visible;
    }

    private async void CloseDeviceSettings_Click(object sender, RoutedEventArgs e)
    {
        DeviceSettingsOverlay.Visibility = Visibility.Collapsed;
        if (_viewModel.IsPlayerVisible)
        {
            NotationWebView.Visibility = Visibility.Visible;
            if (_notationReady && NotationWebView.CoreWebView2 is not null)
            {
                await NotationWebView.CoreWebView2.ExecuteScriptAsync(
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

    private void OpenAudioMixer_Click(object sender, RoutedEventArgs e)
    {
        DeviceSettingsOverlay.Visibility = Visibility.Collapsed;
        ImportWarningsOverlay.Visibility = Visibility.Collapsed;
        NotationWebView.Visibility = Visibility.Collapsed;
        AudioMixerOverlay.Visibility = Visibility.Visible;
    }

    private void CloseAudioMixer_Click(object sender, RoutedEventArgs e)
    {
        AudioMixerOverlay.Visibility = Visibility.Collapsed;
        NotationWebView.Visibility = Visibility.Visible;
    }

    private void OpenImportWarnings_Click(object sender, RoutedEventArgs e)
    {
        DeviceSettingsOverlay.Visibility = Visibility.Collapsed;
        AudioMixerOverlay.Visibility = Visibility.Collapsed;
        NotationWebView.Visibility = Visibility.Collapsed;
        ImportWarningsOverlay.Visibility = Visibility.Visible;
    }

    private void CloseImportWarnings_Click(object sender, RoutedEventArgs e)
    {
        ImportWarningsOverlay.Visibility = Visibility.Collapsed;
        NotationWebView.Visibility = Visibility.Visible;
    }

    private void RebindKey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string actionName)
        {
            _viewModel.StartKeyLearning(actionName);
        }
    }

    private void RebindMidi_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string actionName)
        {
            _viewModel.StartMidiLearning(actionName);
        }
    }

    private void CancelLearning_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.CancelLearning();
    }

    private async void ZoomOut_Click(object sender, RoutedEventArgs e) => await SetNotationZoomAsync(_notationZoom - 10);

    private async void ZoomIn_Click(object sender, RoutedEventArgs e) => await SetNotationZoomAsync(_notationZoom + 10);

    private async void ZoomReset_Click(object sender, RoutedEventArgs e) => await SetNotationZoomAsync(100);

    private async void PreviousScorePage_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsPreviewPlaying || _viewModel.IsPreviewBuilding || _viewModel.IsPreviewPaused || _viewModel.ReadingMode == ScoreReadingMode.Continuous)
            await _viewModel.SeekDisplayPageAsync(-1);
        else if (_notationReady && NotationWebView.CoreWebView2 is not null)
            await NotationWebView.CoreWebView2.ExecuteScriptAsync("window.CadenzaNotation.changePage(-1);");
    }

    private async void NextScorePage_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsPreviewPlaying || _viewModel.IsPreviewBuilding || _viewModel.IsPreviewPaused || _viewModel.ReadingMode == ScoreReadingMode.Continuous)
            await _viewModel.SeekDisplayPageAsync(1);
        else if (_notationReady && NotationWebView.CoreWebView2 is not null)
            await NotationWebView.CoreWebView2.ExecuteScriptAsync("window.CadenzaNotation.changePage(1);");
    }

    private async Task SetNotationZoomAsync(int percent)
    {
        _notationZoom = Math.Clamp(percent, 80, 165);
        _viewModel.NotationZoomPercent = _notationZoom;
        ZoomLevelButton.Content = $"{_notationZoom}%";
        if (_notationReady && NotationWebView.CoreWebView2 is not null)
        {
            await NotationWebView.CoreWebView2.ExecuteScriptAsync(
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

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsTextInputFocused(e)) return;

        if (_viewModel.IsShortcutLearningActive)
        {
            if (e.Key == Key.Escape)
            {
                _viewModel.UnbindAction(_viewModel.LearningActionName);
                e.Handled = true;
                return;
            }

            if (_viewModel.IsKeyLearningActive && e.Key != Key.System)
            {
                _viewModel.ApplyLearnedKey(e.Key.ToString());
                e.Handled = true;
                return;
            }
        }

        var keyStr = e.Key.ToString();
        var keyListen = string.IsNullOrWhiteSpace(_viewModel.KeyShortcutListen) ? "F4" : _viewModel.KeyShortcutListen;
        var keyPractice = string.IsNullOrWhiteSpace(_viewModel.KeyShortcutStartPractice) ? "F5" : _viewModel.KeyShortcutStartPractice;
        var keyPerformance = string.IsNullOrWhiteSpace(_viewModel.KeyShortcutStartPerformance) ? "F6" : _viewModel.KeyShortcutStartPerformance;
        var keyTogglePlay = string.IsNullOrWhiteSpace(_viewModel.KeyShortcutTogglePlay) ? "Space" : _viewModel.KeyShortcutTogglePlay;
        var keyRestart = string.IsNullOrWhiteSpace(_viewModel.KeyShortcutRestartSession) ? "R" : _viewModel.KeyShortcutRestartSession;
        var keyPrevMeasure = string.IsNullOrWhiteSpace(_viewModel.KeyShortcutPreviousMeasure) ? "Left" : _viewModel.KeyShortcutPreviousMeasure;
        var keyNextMeasure = string.IsNullOrWhiteSpace(_viewModel.KeyShortcutNextMeasure) ? "Right" : _viewModel.KeyShortcutNextMeasure;
        var keyPrevPage = string.IsNullOrWhiteSpace(_viewModel.KeyShortcutPreviousPage) ? "PageUp" : _viewModel.KeyShortcutPreviousPage;
        var keyNextPage = string.IsNullOrWhiteSpace(_viewModel.KeyShortcutNextPage) ? "PageDown" : _viewModel.KeyShortcutNextPage;
        var keyDismiss = string.IsNullOrWhiteSpace(_viewModel.KeyShortcutDismissResults) ? "Escape" : _viewModel.KeyShortcutDismissResults;
        var keyRepeat = string.IsNullOrWhiteSpace(_viewModel.KeyShortcutRepeatResults) ? "Enter" : _viewModel.KeyShortcutRepeatResults;

        if (string.Equals(keyStr, keyListen, StringComparison.OrdinalIgnoreCase) || e.Key == Key.F4)
        {
            ListenModeRadio.IsChecked = true;
            await _viewModel.SwitchLessonModeAsync(LessonMode.Listen);
            e.Handled = true;
            return;
        }
        if (string.Equals(keyStr, keyPractice, StringComparison.OrdinalIgnoreCase) || e.Key == Key.F5)
        {
            PracticeModeRadio.IsChecked = true;
            await _viewModel.SwitchLessonModeAsync(LessonMode.WaitForYou);
            e.Handled = true;
            return;
        }
        if (string.Equals(keyStr, keyPerformance, StringComparison.OrdinalIgnoreCase) || e.Key == Key.F6)
        {
            PerformanceModeRadio.IsChecked = true;
            await _viewModel.SwitchLessonModeAsync(LessonMode.TimedPlay);
            e.Handled = true;
            return;
        }
        if ((string.Equals(keyStr, keyRestart, StringComparison.OrdinalIgnoreCase) || e.Key == Key.R) && !e.IsRepeat)
        {
            if (_viewModel.IsLessonActive) _viewModel.EndLesson(false);
            if (_viewModel.IsPreviewPlaying || _viewModel.IsPreviewBuilding) _viewModel.StopPreview();
            _viewModel.CursorBeat = _viewModel.SelectedPreviewStartBeat;
            await _viewModel.StartSelectedModeAsync();
            e.Handled = true;
            return;
        }
        if (string.Equals(keyStr, keyPrevMeasure, StringComparison.OrdinalIgnoreCase) || e.Key == Key.Left)
        {
            await _viewModel.SeekDisplayMeasureAsync(-1);
            e.Handled = true;
            return;
        }
        if (string.Equals(keyStr, keyNextMeasure, StringComparison.OrdinalIgnoreCase) || e.Key == Key.Right)
        {
            await _viewModel.SeekDisplayMeasureAsync(1);
            e.Handled = true;
            return;
        }
        if (string.Equals(keyStr, keyPrevPage, StringComparison.OrdinalIgnoreCase) || e.Key == Key.PageUp)
        {
            await _viewModel.SeekDisplayPageAsync(-1);
            e.Handled = true;
            return;
        }
        if (string.Equals(keyStr, keyNextPage, StringComparison.OrdinalIgnoreCase) || e.Key == Key.PageDown)
        {
            await _viewModel.SeekDisplayPageAsync(1);
            e.Handled = true;
            return;
        }
        if (string.Equals(keyStr, keyDismiss, StringComparison.OrdinalIgnoreCase) && _viewModel.ResultsVisible)
        {
            await NotationWebView.CoreWebView2.ExecuteScriptAsync("window.CadenzaNotation.hideResultsModal();");
            e.Handled = true;
            return;
        }
        if (string.Equals(keyStr, keyRepeat, StringComparison.OrdinalIgnoreCase) && _viewModel.ResultsVisible)
        {
            _ = _viewModel.TriggerAutoRepeatAsync();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Space || e.Key == Key.Enter)
        {
            if (_viewModel.ResultsVisible)
            {
                _ = _viewModel.TriggerAutoRepeatAsync();
                e.Handled = true;
                return;
            }
            if (_viewModel.IsPlayerVisible)
            {
                if (_viewModel.IsLessonActive) _viewModel.StopLesson();
                else await _viewModel.StartSelectedModeAsync();
                e.Handled = true;
                return;
            }
        }
        if (e.Key == Key.Escape && _viewModel.IsLessonActive)
        {
            _viewModel.StopLesson();
            e.Handled = true;
            return;
        }

        if (!e.IsRepeat && _viewModel.UseKeyboardSimulation && ComputerKeyboardPianoMap.MidiNotes.TryGetValue(e.Key, out var midiNote))
        {
            _viewModel.SimulateNoteOn(midiNote);
            e.Handled = true;
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
        if (IsTextInputFocused(e)) return;

        if (_viewModel.UseKeyboardSimulation && ComputerKeyboardPianoMap.MidiNotes.TryGetValue(e.Key, out var midiNote))
        {
            _viewModel.SimulateNoteOff(midiNote);
            e.Handled = true;
        }
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
            await NotationWebView.CoreWebView2.ExecuteScriptAsync(
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
        await _rendererLifecycleGate.WaitAsync();
        try
        {
            await NotationWebView.CoreWebView2.ExecuteScriptAsync(
                $"window.CadenzaNotation.clearPartialFeedback({occurrenceIndex});");
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
                    await NotationWebView.CoreWebView2.ExecuteScriptAsync(script);
                }
            });
            return;
        }
        await NotationWebView.CoreWebView2.ExecuteScriptAsync(script);
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
                    await NotationWebView.CoreWebView2.ExecuteScriptAsync(script);
                }
            });
            return;
        }
        await NotationWebView.CoreWebView2.ExecuteScriptAsync(script);
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
            var script = e.State == "started"
                ? $"window.CadenzaNotation.beginLesson({JsonSerializer.Serialize(e.Mode.ToString())}, {e.StartBeat.ToString(System.Globalization.CultureInfo.InvariantCulture)}, false, {e.RunGeneration}, {_viewModel.OnlyShowFeedbackOnPerformanceEnd.ToString().ToLowerInvariant()});"
                : $"window.CadenzaNotation.finishLesson({(e.State == "completed" ? "true" : "false")}, {e.RunGeneration});";
            await NotationWebView.CoreWebView2.ExecuteScriptAsync(script);
            if (e.State == "started")
                QueueRendererCursor(e.StartBeat, allowReset: true);
            else
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
        else if (e.PropertyName == nameof(MainWindowViewModel.CurrentScore))
        {
            await LoadNotationAsync();
        }
        else if (e.PropertyName is nameof(MainWindowViewModel.CustomScoreScale) or nameof(MainWindowViewModel.CustomScoreMargin) or nameof(MainWindowViewModel.CustomNoteSpacing) or nameof(MainWindowViewModel.CustomBarDensity))
        {
            if (_notationReady && NotationWebView.CoreWebView2 is not null)
            {
                await NotationWebView.CoreWebView2.ExecuteScriptAsync(
                    $"window.CadenzaNotation.setScoreAppearance({_viewModel.CustomScoreScale}, {_viewModel.CustomScoreMargin}, {_viewModel.CustomNoteSpacing}, {_viewModel.CustomBarDensity});");
            }
        }

        if (!_notationReady || NotationWebView.CoreWebView2 is null) return;
        if (e.PropertyName == nameof(MainWindowViewModel.IsPreviewPlaying))
        {
            await _rendererLifecycleGate.WaitAsync();
            try
            {
                if (_viewModel.IsScorePreviewPlaying)
                {
                    _rendererTimelineGeneration++;
                    _lastQueuedCursorBeat = _viewModel.CursorBeat;
                    await NotationWebView.CoreWebView2.ExecuteScriptAsync(
                        $"window.CadenzaNotation.beginTimeline({_viewModel.CursorBeat.ToString(System.Globalization.CultureInfo.InvariantCulture)});");
                    QueueRendererCursor(_viewModel.CursorBeat, allowReset: true);
                }
                else
                {
                    await NotationWebView.CoreWebView2.ExecuteScriptAsync("window.CadenzaNotation.endTimeline();");
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
            await NotationWebView.CoreWebView2.ExecuteScriptAsync(
                $"window.CadenzaNotation.setTempo({_viewModel.EffectiveLessonTempoBpm.ToString(System.Globalization.CultureInfo.InvariantCulture)});");
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.CursorBeat))
        {
            QueueRendererCursor(_viewModel.CursorBeat);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.LessonTempoPercent))
        {
            await NotationWebView.CoreWebView2.ExecuteScriptAsync(
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
                await NotationWebView.CoreWebView2.ExecuteScriptAsync(
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
            await NotationWebView.CoreWebView2.ExecuteScriptAsync(
                $"window.CadenzaNotation.showResultsModal(" +
                $"{JsonSerializer.Serialize(_viewModel.ResultHeadline)}, " +
                $"{JsonSerializer.Serialize(_viewModel.RewardLabel)}, " +
                $"{JsonSerializer.Serialize(_viewModel.AccuracyLabel)}, " +
                $"{JsonSerializer.Serialize(_viewModel.TimingStatValue)}, " +
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
            await NotationWebView.CoreWebView2.ExecuteScriptAsync(
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
            await NotationWebView.CoreWebView2.ExecuteScriptAsync("window.CadenzaNotation.hideResultsModal();");
        }
        finally
        {
            _rendererLifecycleGate.Release();
        }
    }

    private void CancelConflict_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.CancelConflict();
    }

    private void UnbindAction_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.UnbindAction(_viewModel.LearningActionName);
    }

    private void ConfirmConflict_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ConfirmConflict(unbindExisting: false);
    }

    private void UnbindAndConfirmConflict_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ConfirmConflict(unbindExisting: true);
    }

    private void AcceptMultiTap_Click(object sender, RoutedEventArgs e) => _viewModel.AcceptMultiTapPrompt();
    private void CancelMultiTap_Click(object sender, RoutedEventArgs e) => _viewModel.CancelMultiTapPrompt();
    private void IncrementMultiTap_Click(object sender, RoutedEventArgs e) => _viewModel.IncrementMultiTapCount();
    private void DecrementMultiTap_Click(object sender, RoutedEventArgs e) => _viewModel.DecrementMultiTapCount();

    private void Window_Closed(object? sender, EventArgs e)
    {
        _viewModel.CorrectFeedback -= ViewModel_CorrectFeedback;
        _viewModel.NoteFeedback -= ViewModel_NoteFeedback;
        _viewModel.LessonRunStateChanged -= ViewModel_LessonRunStateChanged;
        _viewModel.ResultsPresented -= ViewModel_ResultsPresented;
        _viewModel.AutoRepeatUpdated -= ViewModel_AutoRepeatUpdated;
        _viewModel.ResultsDismissed -= ViewModel_ResultsDismissed;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        CompositionTarget.Rendering -= CompositionTarget_Rendering;
        if (NotationWebView.CoreWebView2 is not null)
        {
            NotationWebView.CoreWebView2.WebMessageReceived -= NotationWebView_WebMessageReceived;
        }
        _viewModel.Dispose();
    }
}
