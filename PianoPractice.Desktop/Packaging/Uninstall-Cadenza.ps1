$ErrorActionPreference = 'Stop'

$localProgramsDirectory = [IO.Path]::GetFullPath(
    (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs'))
$installDirectory = [IO.Path]::GetFullPath(
    (Join-Path $localProgramsDirectory 'Cadenza'))

if ([IO.Path]::GetDirectoryName($installDirectory) -ne $localProgramsDirectory) {
    throw "Refusing to remove an unexpected install directory: $installDirectory"
}

$shortcutPath = Join-Path `
    ([Environment]::GetFolderPath('ApplicationData')) `
    'Microsoft\Windows\Start Menu\Programs\Cadenza.lnk'
$registryKeys = @(
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\Cadenza.exe',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Cadenza',
    'HKCU:\Software\Classes\Applications\Cadenza.exe'
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
