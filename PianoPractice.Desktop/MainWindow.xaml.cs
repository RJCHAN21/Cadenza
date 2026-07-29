using System.IO;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
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
        _viewModel.LessonRunStateChanged += ViewModel_LessonRunStateChanged;
        _viewModel.ResultsPresented += ViewModel_ResultsPresented;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        CompositionTarget.Rendering += CompositionTarget_Rendering;
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
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
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
            $"window.CadenzaNotation.setZoom({_viewModel.NotationZoomPercent}); window.CadenzaNotation.setHintMode({(_viewModel.HintModeEnabled ? "true" : "false")}); window.CadenzaNotation.setHandMode({JsonSerializer.Serialize(_viewModel.SelectedMode.ToString())}); window.CadenzaNotation.setTempo({_viewModel.EffectiveLessonTempoBpm.ToString(System.Globalization.CultureInfo.InvariantCulture)});");
    }

    private void ImportScore_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import a MusicXML score",
            Filter = "MusicXML scores|*.mxl;*.musicxml;*.xml|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _viewModel.LoadScore(dialog.FileName);
            _ = LoadNotationAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Score import failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

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

    private void OpenLesson_Click(object sender, RoutedEventArgs e) => _viewModel.OpenCurrentLesson();

    private void BackToDashboard_Click(object sender, RoutedEventArgs e) => _viewModel.ShowDashboard();

    private void OpenDeviceSettings_Click(object sender, RoutedEventArgs e)
    {
        AudioMixerOverlay.Visibility = Visibility.Collapsed;
        ImportWarningsOverlay.Visibility = Visibility.Collapsed;
        NotationWebView.Visibility = Visibility.Collapsed;
        DeviceSettingsOverlay.Visibility = Visibility.Visible;
    }

    private void CloseDeviceSettings_Click(object sender, RoutedEventArgs e)
    {
        DeviceSettingsOverlay.Visibility = Visibility.Collapsed;
        NotationWebView.Visibility = Visibility.Visible;
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

    private async void ZoomOut_Click(object sender, RoutedEventArgs e) => await SetNotationZoomAsync(_notationZoom - 10);

    private async void ZoomIn_Click(object sender, RoutedEventArgs e) => await SetNotationZoomAsync(_notationZoom + 10);

    private async void ZoomReset_Click(object sender, RoutedEventArgs e) => await SetNotationZoomAsync(100);

    private async void PreviousScorePage_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.ReadingMode == ScoreReadingMode.Continuous)
            await _viewModel.SeekDisplayMeasureAsync(-1);
        else if (_notationReady && NotationWebView.CoreWebView2 is not null)
            await NotationWebView.CoreWebView2.ExecuteScriptAsync("window.CadenzaNotation.changePage(-1);");
    }

    private async void NextScorePage_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.ReadingMode == ScoreReadingMode.Continuous)
            await _viewModel.SeekDisplayMeasureAsync(1);
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

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is not System.Windows.Controls.TextBox)
        {
            if (e.Key == Key.F4)
            {
                ListenModeRadio.IsChecked = true;
                await _viewModel.SwitchLessonModeAsync(LessonMode.Listen);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F5)
            {
                PracticeModeRadio.IsChecked = true;
                await _viewModel.SwitchLessonModeAsync(LessonMode.WaitForYou);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F6)
            {
                PerformanceModeRadio.IsChecked = true;
                await _viewModel.SwitchLessonModeAsync(LessonMode.TimedPlay);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Space && _viewModel.IsPlayerVisible)
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
        if (_viewModel.UseKeyboardSimulation && ComputerKeyboardPianoMap.MidiNotes.TryGetValue(e.Key, out var midiNote))
        {
            _viewModel.SimulateNoteOff(midiNote);
            e.Handled = true;
        }
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
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
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
        var dispatchId = Interlocked.Increment(ref _rendererFeedbackDispatchId);
        await _rendererLifecycleGate.WaitAsync();
        try
        {
            await NotationWebView.CoreWebView2.ExecuteScriptAsync(
                $"window.CadenzaNotation.showFeedback({JsonSerializer.Serialize(e.Kind)}, {e.Beat.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {(e.MidiNoteNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null")}, {dispatchId}, {e.RunGeneration}, {e.EventId}, {e.OccurrenceIndex}, {e.StaffNumber});");
        }
        finally
        {
            _rendererLifecycleGate.Release();
        }
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
                ? $"window.CadenzaNotation.beginLesson({JsonSerializer.Serialize(e.Mode.ToString())}, {e.StartBeat.ToString(System.Globalization.CultureInfo.InvariantCulture)}, false, {e.RunGeneration});"
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

    private void ViewModel_ResultsPresented(object? sender, EventArgs e)
    {
        NotationWebView.Visibility = Visibility.Collapsed;
        if (!SystemParameters.ClientAreaAnimation)
        {
            ResultsPanel.Opacity = 1;
            ResultsScale.ScaleX = ResultsScale.ScaleY = 1;
            return;
        }

        ResultsPanel.Opacity = 0;
        ResultsScale.ScaleX = ResultsScale.ScaleY = .94;
        ResultsPanel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
        var scale = new DoubleAnimation(.94, 1, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new BackEase { Amplitude = .22, EasingMode = EasingMode.EaseOut }
        };
        ResultsScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scale);
        ResultsScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scale);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _viewModel.CorrectFeedback -= ViewModel_CorrectFeedback;
        _viewModel.NoteFeedback -= ViewModel_NoteFeedback;
        _viewModel.LessonRunStateChanged -= ViewModel_LessonRunStateChanged;
        _viewModel.ResultsPresented -= ViewModel_ResultsPresented;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        CompositionTarget.Rendering -= CompositionTarget_Rendering;
        if (NotationWebView.CoreWebView2 is not null)
        {
            NotationWebView.CoreWebView2.WebMessageReceived -= NotationWebView_WebMessageReceived;
        }
        _viewModel.Dispose();
    }
}
