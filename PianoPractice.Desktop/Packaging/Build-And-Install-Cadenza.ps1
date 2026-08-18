param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$projectPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\PianoPractice.Desktop.csproj'))
$publishRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\publish'))
$publishDirectory = [IO.Path]::GetFullPath((Join-Path $publishRoot 'Cadenza'))
$localProgramsDirectory = [IO.Path]::GetFullPath(
    (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs'))
$installDirectory = [IO.Path]::GetFullPath((Join-Path $localProgramsDirectory 'Cadenza'))
$installedExecutable = Join-Path $installDirectory 'Cadenza.exe'

if ([IO.Path]::GetDirectoryName($publishDirectory) -ne $publishRoot) {
    throw "Refusing to clean an unexpected publish directory: $publishDirectory"
}

if ([IO.Path]::GetDirectoryName($installDirectory) -ne $localProgramsDirectory) {
    throw "Refusing to update an unexpected install directory: $installDirectory"
}

if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

& dotnet publish $projectPath `
    --configuration $Configuration `
    --no-self-contained `
    --output $publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw "Cadenza publish failed with exit code $LASTEXITCODE."
}

$requiredPublishedFiles = @(
    'Cadenza.exe',
    'Cadenza.dll',
    'Assets\Verovio\player.html',
    'Uninstall-Cadenza.ps1'
)
foreach ($relativePath in $requiredPublishedFiles) {
    $publishedPath = Join-Path $publishDirectory $relativePath
    if (-not (Test-Path -LiteralPath $publishedPath -PathType Leaf)) {
        throw "Published Cadenza is incomplete; missing $relativePath."
    }
}

$runningInstalledProcess = Get-CimInstance Win32_Process -Filter "Name='Cadenza.exe'" |
    Where-Object {
        $_.ExecutablePath -and
        [IO.Path]::GetFullPath($_.ExecutablePath).Equals(
            $installedExecutable,
            [StringComparison]::OrdinalIgnoreCase)
    }
if ($runningInstalledProcess) {
    $processIds = ($runningInstalledProcess.ProcessId -join ', ')
    throw "Close the installed Cadenza before updating it (process $processIds). The Release publish is ready at $publishDirectory."
}

New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $publishDirectory '*') -Destination $installDirectory -Recurse -Force

foreach ($publishedFile in Get-ChildItem -LiteralPath $publishDirectory -File -Recurse) {
    $relativePath = [IO.Path]::GetRelativePath($publishDirectory, $publishedFile.FullName)
    $installedFile = Join-Path $installDirectory $relativePath
    if (-not (Test-Path -LiteralPath $installedFile -PathType Leaf)) {
        throw "Installation verification failed; missing $relativePath."
    }
    $publishedHash = (Get-FileHash -LiteralPath $publishedFile.FullName -Algorithm SHA256).Hash
    $installedHash = (Get-FileHash -LiteralPath $installedFile -Algorithm SHA256).Hash
    if ($publishedHash -ne $installedHash) {
        throw "Installation verification failed; hash mismatch for $relativePath."
    }
}

$displayVersion = (Get-Item -LiteralPath $installedExecutable).VersionInfo.FileVersion
if ([string]::IsNullOrWhiteSpace($displayVersion)) {
    $displayVersion = '1.0.0'
} else {
    $displayVersion = $displayVersion -replace '\.0$', ''
}
$estimatedSize = [Math]::Ceiling(
    ((Get-ChildItem -LiteralPath $installDirectory -File -Recurse | Measure-Object Length -Sum).Sum) / 1KB)
$uninstallCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $installDirectory 'Uninstall-Cadenza.ps1')`""

$appPathKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\Cadenza.exe'
New-Item -Path $appPathKey -Force | Out-Null
Set-Item -Path $appPathKey -Value $installedExecutable
New-ItemProperty -Path $appPathKey -Name 'Path' -Value $installDirectory -PropertyType String -Force | Out-Null

$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Cadenza'
New-Item -Path $uninstallKey -Force | Out-Null
$uninstallValues = @{
    DisplayName = 'Cadenza'
    DisplayVersion = $displayVersion
    Publisher = 'RJGAMES'
    InstallLocation = $installDirectory
    DisplayIcon = "$installedExecutable,0"
    UninstallString = $uninstallCommand
    QuietUninstallString = $uninstallCommand
    URLInfoAbout = 'https://github.com/RJCHAN21/Cadenza'
    InstallDate = Get-Date -Format 'yyyyMMdd'
    EstimatedSize = [int]$estimatedSize
    NoModify = 1
    NoRepair = 1
}
foreach ($entry in $uninstallValues.GetEnumerator()) {
    $propertyType = if ($entry.Value -is [int]) { 'DWord' } else { 'String' }
    New-ItemProperty -Path $uninstallKey -Name $entry.Key -Value $entry.Value -PropertyType $propertyType -Force | Out-Null
}

$applicationKey = 'HKCU:\Software\Classes\Applications\Cadenza.exe'
New-Item -Path $applicationKey -Force | Out-Null
New-ItemProperty -Path $applicationKey -Name 'FriendlyAppName' -Value 'Cadenza' -PropertyType String -Force | Out-Null
$defaultIconKey = Join-Path $applicationKey 'DefaultIcon'
New-Item -Path $defaultIconKey -Force | Out-Null
Set-Item -Path $defaultIconKey -Value "$installedExecutable,0"
$openCommandKey = Join-Path $applicationKey 'shell\open\command'
New-Item -Path $openCommandKey -Force | Out-Null
Set-Item -Path $openCommandKey -Value "`"$installedExecutable`""

$shortcutPath = Join-Path `
    ([Environment]::GetFolderPath('ApplicationData')) `
    'Microsoft\Windows\Start Menu\Programs\Cadenza.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $installedExecutable
$shortcut.WorkingDirectory = $installDirectory
$shortcut.IconLocation = "$installedExecutable,0"
$shortcut.Save()

Write-Host "Cadenza $Configuration build installed and verified at $installedExecutable"
