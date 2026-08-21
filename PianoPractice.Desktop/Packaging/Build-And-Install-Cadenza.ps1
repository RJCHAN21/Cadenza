param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$projectPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\PianoPractice.Desktop.csproj'))
$publishRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\publish'))
$isDebug = $Configuration -eq 'Debug'
$displayName = if ($isDebug) { 'Cadenza (DEBUG)' } else { 'Cadenza' }
$installDirectoryName = $displayName
$registryName = if ($isDebug) { 'Cadenza.Debug' } else { 'Cadenza' }
$appPathExecutableName = "$registryName.exe"
$shortcutFileName = "$displayName.lnk"
$userDataDirectoryName = if ($isDebug) { 'CadenzaPianoStudio-Debug' } else { 'CadenzaPianoStudio' }
$publishDirectory = [IO.Path]::GetFullPath((Join-Path $publishRoot $Configuration))
$localProgramsDirectory = [IO.Path]::GetFullPath(
    (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs'))
$installDirectory = [IO.Path]::GetFullPath((Join-Path $localProgramsDirectory $installDirectoryName))
$installedExecutable = Join-Path $installDirectory 'Cadenza.exe'
$debugInstalledExecutable = Join-Path $localProgramsDirectory 'Cadenza (DEBUG)\Cadenza.exe'
$userDataDirectory = [IO.Path]::GetFullPath(
    (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) $userDataDirectoryName))
$releaseUserDataDirectory = [IO.Path]::GetFullPath(
    (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CadenzaPianoStudio'))
$debugUserDataDirectory = [IO.Path]::GetFullPath(
    (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CadenzaPianoStudio-Debug'))
$userDataBackupRoot = [IO.Path]::GetFullPath(
    (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "$userDataDirectoryName-Backups"))
$debugUserDataBackupRoot = [IO.Path]::GetFullPath(
    (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CadenzaPianoStudio-Debug-Backups'))
$userDataRelativePaths = @(
    'profile.json',
    'profile.json.bak',
    'library_manifest.json',
    'library_manifest.json.bak',
    'Library'
)
$releaseJsonDirectory = $releaseUserDataDirectory.Replace('\', '\\')
$debugJsonDirectory = $debugUserDataDirectory.Replace('\', '\\')

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

$runningInstalledProcesses = Get-CimInstance Win32_Process -Filter "Name='Cadenza.exe'" |
    Where-Object {
        $_.ExecutablePath -and
        [IO.Path]::GetFullPath($_.ExecutablePath).Equals(
            $installedExecutable,
            [StringComparison]::OrdinalIgnoreCase)
    }
if ($runningInstalledProcesses) {
    $processIds = ($runningInstalledProcesses.ProcessId -join ', ')
    throw "Close $displayName before updating it (process $processIds). The $Configuration publish is ready at $publishDirectory."
}

if (-not $isDebug) {
    $runningDebugProcesses = Get-CimInstance Win32_Process -Filter "Name='Cadenza.exe'" |
        Where-Object {
            $_.ExecutablePath -and
            [IO.Path]::GetFullPath($_.ExecutablePath).Equals(
                $debugInstalledExecutable,
                [StringComparison]::OrdinalIgnoreCase)
        }
    if ($runningDebugProcesses) {
        $processIds = ($runningDebugProcesses.ProcessId -join ', ')
        throw "Close Cadenza (DEBUG) before synchronizing its data (process $processIds). The Release publish is ready at $publishDirectory."
    }
}

$userDataBackupDirectory = $null
if (Test-Path -LiteralPath $userDataDirectory -PathType Container) {
    $userDataBackupDirectory = Join-Path $userDataBackupRoot (Get-Date -Format 'yyyyMMdd-HHmmssfff')
    New-Item -ItemType Directory -Path $userDataBackupDirectory -Force | Out-Null
    foreach ($relativePath in $userDataRelativePaths) {
        $sourcePath = Join-Path $userDataDirectory $relativePath
        if (Test-Path -LiteralPath $sourcePath) {
            Copy-Item -LiteralPath $sourcePath -Destination $userDataBackupDirectory -Recurse -Force
        }
    }
}

$debugSyncBackupDirectory = $null
if (-not $isDebug -and (Test-Path -LiteralPath $debugUserDataDirectory -PathType Container)) {
    $debugSyncBackupDirectory = Join-Path $debugUserDataBackupRoot (Get-Date -Format 'yyyyMMdd-HHmmssfff')
    New-Item -ItemType Directory -Path $debugSyncBackupDirectory -Force | Out-Null
    foreach ($relativePath in $userDataRelativePaths) {
        $sourcePath = Join-Path $debugUserDataDirectory $relativePath
        if (Test-Path -LiteralPath $sourcePath) {
            Copy-Item -LiteralPath $sourcePath -Destination $debugSyncBackupDirectory -Recurse -Force
        }
    }
}

$releaseDataAvailable = Test-Path -LiteralPath $releaseUserDataDirectory -PathType Container
if ($isDebug -and -not $releaseDataAvailable) {
    throw "Debug data synchronization requires Release data at $releaseUserDataDirectory."
}

if ($releaseDataAvailable) {
    New-Item -ItemType Directory -Path $debugUserDataDirectory -Force | Out-Null
    foreach ($relativePath in $userDataRelativePaths) {
        $sourcePath = Join-Path $releaseUserDataDirectory $relativePath
        $destinationPath = Join-Path $debugUserDataDirectory $relativePath

        if (Test-Path -LiteralPath $destinationPath) {
            Remove-Item -LiteralPath $destinationPath -Recurse -Force
        }
        if (-not (Test-Path -LiteralPath $sourcePath)) {
            continue
        }

        Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Recurse -Force
        if (Test-Path -LiteralPath $sourcePath -PathType Leaf) {
            $sourceContent = [IO.File]::ReadAllText($sourcePath)
            $destinationContent = [IO.File]::ReadAllText($destinationPath)
            $destinationContent = $destinationContent.Replace(
                $releaseJsonDirectory,
                $debugJsonDirectory,
                [StringComparison]::OrdinalIgnoreCase)
            [IO.File]::WriteAllText(
                $destinationPath,
                $destinationContent,
                [Text.UTF8Encoding]::new($false))

            $normalizedDestinationContent = $destinationContent.Replace(
                $debugJsonDirectory,
                $releaseJsonDirectory,
                [StringComparison]::OrdinalIgnoreCase)
            if ($sourceContent -cne $normalizedDestinationContent) {
                throw "Debug data synchronization failed; content mismatch for $relativePath."
            }
            continue
        }

        $sourceFiles = @(Get-ChildItem -LiteralPath $sourcePath -File -Recurse)
        $destinationFiles = @(Get-ChildItem -LiteralPath $destinationPath -File -Recurse)
        if ($sourceFiles.Count -ne $destinationFiles.Count) {
            throw "Debug data synchronization failed; file-count mismatch for $relativePath."
        }
        foreach ($sourceFile in $sourceFiles) {
            $childRelativePath = [IO.Path]::GetRelativePath($sourcePath, $sourceFile.FullName)
            $destinationFile = Join-Path $destinationPath $childRelativePath
            if (-not (Test-Path -LiteralPath $destinationFile -PathType Leaf) -or
                (Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $destinationFile -Algorithm SHA256).Hash) {
                throw "Debug data synchronization failed; file mismatch for $relativePath\$childRelativePath."
            }
        }
    }

    $debugLibraryDirectory = [IO.Path]::GetFullPath((Join-Path $debugUserDataDirectory 'Library'))
    $debugLibraryRoot = $debugLibraryDirectory.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $debugManifestPath = Join-Path $debugUserDataDirectory 'library_manifest.json'
    if (Test-Path -LiteralPath $debugManifestPath -PathType Leaf) {
        $debugManifest = @(Get-Content -LiteralPath $debugManifestPath -Raw | ConvertFrom-Json)
        foreach ($item in $debugManifest) {
            $storedFilePath = [IO.Path]::GetFullPath([string]$item.StoredFilePath)
            if (-not $storedFilePath.StartsWith($debugLibraryRoot, [StringComparison]::OrdinalIgnoreCase) -or
                -not (Test-Path -LiteralPath $storedFilePath -PathType Leaf)) {
                throw "Debug data synchronization failed; invalid managed score path for $($item.OriginalFileName)."
            }
        }
    }
    Write-Host "Release profile and library synchronized to $debugUserDataDirectory"
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

$appPathKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\$appPathExecutableName"
New-Item -Path $appPathKey -Force | Out-Null
Set-Item -Path $appPathKey -Value $installedExecutable
New-ItemProperty -Path $appPathKey -Name 'Path' -Value $installDirectory -PropertyType String -Force | Out-Null

$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$registryName"
New-Item -Path $uninstallKey -Force | Out-Null
$uninstallValues = @{
    DisplayName = $displayName
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

$applicationKey = "HKCU:\Software\Classes\Applications\$appPathExecutableName"
New-Item -Path $applicationKey -Force | Out-Null
New-ItemProperty -Path $applicationKey -Name 'FriendlyAppName' -Value $displayName -PropertyType String -Force | Out-Null
$defaultIconKey = Join-Path $applicationKey 'DefaultIcon'
New-Item -Path $defaultIconKey -Force | Out-Null
Set-Item -Path $defaultIconKey -Value "$installedExecutable,0"
$openCommandKey = Join-Path $applicationKey 'shell\open\command'
New-Item -Path $openCommandKey -Force | Out-Null
Set-Item -Path $openCommandKey -Value "`"$installedExecutable`""

$shortcutPath = Join-Path `
    ([Environment]::GetFolderPath('ApplicationData')) `
    "Microsoft\Windows\Start Menu\Programs\$shortcutFileName"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $installedExecutable
$shortcut.WorkingDirectory = $installDirectory
$shortcut.IconLocation = "$installedExecutable,0"
$shortcut.Save()

if ($userDataBackupDirectory) {
    Write-Host "Existing Cadenza user data was backed up to $userDataBackupDirectory"
}
if ($debugSyncBackupDirectory) {
    Write-Host "Existing Debug data was backed up to $debugSyncBackupDirectory"
}
Write-Host "$displayName installed and verified at $installedExecutable"
