$ErrorActionPreference = 'Stop'

$localProgramsDirectory = [IO.Path]::GetFullPath(
    (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs'))
$installDirectory = [IO.Path]::GetFullPath($PSScriptRoot)
$isDebug = [IO.Path]::GetFileName($installDirectory) -eq 'Cadenza (DEBUG)'
$displayName = if ($isDebug) { 'Cadenza (DEBUG)' } else { 'Cadenza' }
$registryName = if ($isDebug) { 'Cadenza.Debug' } else { 'Cadenza' }
$appPathExecutableName = "$registryName.exe"

if ([IO.Path]::GetDirectoryName($installDirectory) -ne $localProgramsDirectory) {
    throw "Refusing to remove an unexpected install directory: $installDirectory"
}

$shortcutPath = Join-Path `
    ([Environment]::GetFolderPath('ApplicationData')) `
    "Microsoft\Windows\Start Menu\Programs\$displayName.lnk"
$registryKeys = @(
    "HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\$appPathExecutableName",
    "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$registryName",
    "HKCU:\Software\Classes\Applications\$appPathExecutableName"
)

if (Test-Path -LiteralPath $shortcutPath) {
    Remove-Item -LiteralPath $shortcutPath -Force
}

foreach ($registryKey in $registryKeys) {
    if (Test-Path -LiteralPath $registryKey) {
        Remove-Item -LiteralPath $registryKey -Recurse -Force
    }
}

if (Test-Path -LiteralPath $installDirectory) {
    Remove-Item -LiteralPath $installDirectory -Recurse -Force
}
