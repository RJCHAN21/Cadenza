using System.IO;

namespace PianoPractice.Desktop;

/// <summary>
/// Provides configuration-specific local storage so Debug testing cannot modify Release data.
/// </summary>
internal static class AppStoragePaths
{
#if DEBUG
    private const string ProductDirectoryName = "CadenzaPianoStudio-Debug";
    private const string DiagnosticsDirectoryName = "Cadenza-Debug";
#else
    private const string ProductDirectoryName = "CadenzaPianoStudio";
    private const string DiagnosticsDirectoryName = "Cadenza";
#endif

    public static string ProductDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProductDirectoryName);

    public static string DiagnosticsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        DiagnosticsDirectoryName,
        "Diagnostics");
}
