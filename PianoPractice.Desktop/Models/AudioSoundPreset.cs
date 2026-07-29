namespace PianoPractice.Desktop.Models;

public sealed record AudioSoundPreset(string Id, string Name, int PatchNumber, bool IsSoftSynth = false)
{
    public static readonly AudioSoundPreset AcousticGrand = new("acoustic_grand", "Acoustic Grand Piano (WinMM)", 0);
    public static readonly AudioSoundPreset SoftSynth = new("soft_synth", "Built-in Soft Synth (Harmonic Soft Tone)", -1, IsSoftSynth: true);
    public static readonly AudioSoundPreset BrightPiano = new("bright_piano", "Bright Acoustic Piano", 1);
    public static readonly AudioSoundPreset ElectricGrand = new("electric_grand", "Electric Grand Piano", 2);
    public static readonly AudioSoundPreset HonkyTonk = new("honky_tonk", "Honky-Tonk Piano", 3);
    public static readonly AudioSoundPreset ElectricPiano = new("electric_piano", "Electric Piano (Rhodes)", 4);
    public static readonly AudioSoundPreset Harpsichord = new("harpsichord", "Harpsichord", 6);
    public static readonly AudioSoundPreset ChurchOrgan = new("church_organ", "Church Organ", 19);

    public static IReadOnlyList<AudioSoundPreset> AllPresets { get; } =
    [
        AcousticGrand,
        SoftSynth,
        BrightPiano,
        ElectricGrand,
        HonkyTonk,
        ElectricPiano,
        Harpsichord,
        ChurchOrgan
    ];

    public static AudioSoundPreset FromId(string? id, AudioSoundPreset fallback)
    {
        if (string.IsNullOrWhiteSpace(id)) return fallback;
        return AllPresets.FirstOrDefault(preset => string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase)) ?? fallback;
    }

    public override string ToString() => Name;
}
