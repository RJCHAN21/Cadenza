using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace PianoPractice.Desktop;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
    }

    private static void App_DispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        if (!IsPlaybackStartupFailure(e.Exception)) return;

        try
        {
            var diagnosticsDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Cadenza",
                "Diagnostics");
            Directory.CreateDirectory(diagnosticsDirectory);
            File.AppendAllText(
                Path.Combine(diagnosticsDirectory, "playback-startup-errors.log"),
                $"[{DateTimeOffset.Now:O}] {e.Exception}\n\n");
        }
        catch
        {
            // Diagnostics must never replace the original user-facing error.
        }

        MessageBox.Show(
            $"Playback could not start. Cadenza stayed open so you can adjust the mode or audio settings and try again.\n\n{e.Exception.Message}",
            "Playback startup failed",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static bool IsPlaybackStartupFailure(Exception exception)
    {
        if (exception is OperationCanceledException) return true;

        var trace = exception.ToString();
        return trace.Contains("PianoAudioService", StringComparison.Ordinal) ||
               trace.Contains("MidiOutSynthService", StringComparison.Ordinal) ||
               trace.Contains("PreparePerformanceAudioAsync", StringComparison.Ordinal) ||
               trace.Contains("StartSelectedModeCoreAsync", StringComparison.Ordinal) ||
               trace.Contains("TogglePreview_Click", StringComparison.Ordinal) ||
               trace.Contains("StartLesson_Click", StringComparison.Ordinal);
    }
}
