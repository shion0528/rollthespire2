$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\packaging\SteamGameDiscovery.ps1')
$script:taskInstallerCheckCount = 0
function Assert-Rt2InstallerCheck {
    param([bool]$Condition, [string]$Message)
    $script:taskInstallerCheckCount++
    if (-not $Condition) { throw ('Installer check failed: ' + $Message) }
}
function Assert-Rt2InstallerThrows {
    param([scriptblock]$Action, [string]$Message)
    $taskCaught = $false
    try { & $Action } catch {
        $taskCaught = $true
        Assert-Rt2InstallerCheck ($_.Exception.Message -like ('*' + $Message + '*')) ('Unexpected error: ' + $_.Exception.Message)
    }
    Assert-Rt2InstallerCheck $taskCaught ('Expected error containing: ' + $Message)
}
function Write-Rt2FixtureFile {
    param([string]$Path, [string]$Text)
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}
function New-Rt2FixtureGame {
    param([string]$Library)
    $taskFixtureGame = Join-Path $Library 'steamapps\common\Slay the Spire 2'
    Write-Rt2FixtureFile (Join-Path $taskFixtureGame 'SlayTheSpire2.exe') 'fixture only; never executed'
    return $taskFixtureGame
}
function Get-Rt2FixtureHash { param([string]$Path); (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }

# Isolate all installer runs from real processes and real game files. The
# default-discovery/running-game check is performed separately by the maintainer.
$taskFixtureProcessState = @{ Running = $false; Executable = '' }
function Get-Process {
    [CmdletBinding()]
    param([string]$Name)
    if ($taskFixtureProcessState.Running -and $Name -eq 'SlayTheSpire2') {
        [pscustomobject]@{ Path = $taskFixtureProcessState.Executable }
    }
}

$taskFixtureRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('rt2-steam-installer-' + [guid]::NewGuid().ToString('N'))))
[void][IO.Directory]::CreateDirectory($taskFixtureRoot)
try {
    # Pure VDF fixtures cover current objects, legacy strings, nested app IDs,
    # comments, quoted values, escaped backslashes and unrelated metadata.
    $taskLegacyVdf = @'
"LibraryFolders"
{
  "TimeNextStatsReport" "123"
  "1" "D:\\Steam Library"
  "2" "E:\SteamLibrary"
}
'@
    $taskParsed = @(ConvertFrom-Rt2SteamLibraryVdf $taskLegacyVdf)
    Assert-Rt2InstallerCheck ($taskParsed.Count -eq 2 -and $taskParsed -contains 'D:\Steam Library' -and
        $taskParsed -contains 'E:\SteamLibrary') 'Legacy VDF and literal/escaped backslashes'
    $taskModernVdf = @'
// A comment must not turn "path" "Z:\\Comment" into a library.
"libraryfolders"
{
  "0" { "path" "C:\\Steam" "label" "A \"quoted\" label" "apps" { "2868840" "123456" "480" "42" } }
  "1" { "path" "F:\\Other Library" "apps" { "999999" "1" } }
  "2" { "label" "missing path" }
  "metadata" { "path" "Z:\\Unrelated" }
}
'@
    $taskParsed = @(ConvertFrom-Rt2SteamLibraryVdf $taskModernVdf)
    Assert-Rt2InstallerCheck ($taskParsed.Count -eq 2 -and $taskParsed -contains 'C:\Steam' -and
        $taskParsed -contains 'F:\Other Library') 'Current VDF only admits indexed library paths'
    Assert-Rt2InstallerCheck (@(ConvertFrom-Rt2SteamLibraryVdf '"unknown" { "path" "D:\\Unknown" }').Count -eq 0) 'Unknown VDF format'
    Assert-Rt2InstallerThrows { ConvertFrom-Rt2SteamLibraryVdf '"libraryfolders" { "0" { "path" "D:\\Broken" }' } 'incomplete object'
    Assert-Rt2InstallerCheck ($null -eq (Get-Rt2NormalizedSteamPath 'relative\SteamLibrary')) 'Relative VDF path is ignored'
    Assert-Rt2InstallerCheck ((Get-Rt2NormalizedSteamPath 'D:\Steam Library\') -eq 'D:\Steam Library') 'Other drive normalization'
    Assert-Rt2InstallerCheck ((Get-Rt2NormalizedSteamPath '\\server\share\Steam Library') -eq '\\server\share\Steam Library') 'UNC path normalization'

    $taskUnicode = [string][char]0x6e38 + [char]0x620f
    $taskSteamRoot = Join-Path $taskFixtureRoot 'Steam Root'
    $taskLibraryA = Join-Path $taskFixtureRoot ($taskUnicode + ' Library A')
    $taskLibraryB = Join-Path $taskFixtureRoot 'Other Games Library'
    $taskMissing = Join-Path $taskFixtureRoot 'Missing Library'
    $taskGameA = New-Rt2FixtureGame $taskLibraryA
    Write-Rt2FixtureFile (Join-Path $taskLibraryB 'steamapps\common\Another Game\AnotherGame.exe') 'unrelated game'
    $taskRootEscaped = $taskSteamRoot.Replace('\', '\\')
    $taskAEscaped = $taskLibraryA.Replace('\', '\\')
    $taskBEscaped = $taskLibraryB.Replace('\', '\\')
    $taskMissingEscaped = $taskMissing.Replace('\', '\\')
    Write-Rt2FixtureFile (Join-Path $taskSteamRoot 'steamapps\libraryfolders.vdf') @"
"libraryfolders"
{
  "0" { "path" "$taskRootEscaped" }
  "1" { "path" "$taskAEscaped" "apps" { "2868840" "10" "480" "100" } }
  "2" { "path" "$taskBEscaped" "apps" { "999999" "100" } }
  "3" { "path" "$taskMissingEscaped" }
  "4" { "path" "relative\\ignored" }
}
"@
    # An old-format nested list also tests cycles, duplicate library entries and
    # the historical config location without a recursive directory traversal.
    Write-Rt2FixtureFile (Join-Path $taskLibraryA 'config\libraryfolders.vdf') @"
"LibraryFolders" { "1" "$taskRootEscaped" "2" "$taskAEscaped" }
"@
    $taskFound = @(Find-Rt2SteamGameInstallations -SteamRoots @($taskSteamRoot, $taskLibraryA, $taskMissing))
    Assert-Rt2InstallerCheck ($taskFound.Count -eq 1 -and $taskFound[0] -eq $taskGameA) 'Find unique game across libraries; ignore other/missing games and cycles'
    Assert-Rt2InstallerCheck ((Resolve-Rt2GameRoot -SteamRoots @($taskSteamRoot)) -eq $taskGameA) 'Default fixture resolution'
    Assert-Rt2InstallerCheck (@(Find-Rt2SteamGameInstallations -SteamRoots @($taskMissing)).Count -eq 0) 'Missing roots do not produce games'
    Assert-Rt2InstallerThrows { Resolve-Rt2GameRoot -SteamRoots @($taskMissing) } 'Pass -GameRoot'
    $taskSecondGame = New-Rt2FixtureGame $taskSteamRoot
    Assert-Rt2InstallerThrows { Resolve-Rt2GameRoot -SteamRoots @($taskSteamRoot) } 'Multiple Slay the Spire 2 installations'
    try { Resolve-Rt2GameRoot -SteamRoots @($taskSteamRoot) } catch {
        Assert-Rt2InstallerCheck ($_.Exception.Message.Contains($taskGameA) -and $_.Exception.Message.Contains($taskSecondGame)) 'Ambiguity error lists every candidate'
    }
    Assert-Rt2InstallerCheck ((Resolve-Rt2GameRoot -GameRoot $taskGameA -SteamRoots @($taskSteamRoot)) -eq $taskGameA) 'Explicit game overrides multiple detected installations'
    Assert-Rt2InstallerThrows { Resolve-Rt2GameRoot -GameRoot $taskMissing -SteamRoots @($taskSteamRoot) } 'explicit -GameRoot'
    $taskExeDirectory = Join-Path $taskFixtureRoot 'Not A Game'
    [void][IO.Directory]::CreateDirectory((Join-Path $taskExeDirectory 'SlayTheSpire2.exe'))
    Assert-Rt2InstallerThrows { Resolve-Rt2GameRoot -GameRoot $taskExeDirectory -SteamRoots @() } 'explicit -GameRoot'

    # Run the actual installer against a synthetic package and synthetic game.
    $taskPackage = Join-Path $taskFixtureRoot 'Package With Spaces'
    [void][IO.Directory]::CreateDirectory($taskPackage)
    foreach ($taskScript in @('Install-PerformanceBuild.ps1', 'SteamGameDiscovery.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('..\packaging\' + $taskScript)) -Destination (Join-Path $taskPackage $taskScript)
    }
    $taskInstaller = Join-Path $taskPackage 'Install-PerformanceBuild.ps1'
    $taskPayload = Join-Path $taskPackage 'RolltheSpire2'
    $taskMod = Join-Path $taskGameA 'mods\RolltheSpire2'
    $taskNames = @('RolltheSpire2.dll', 'RolltheSpire2.json')
    $taskExpected = @{}
    $taskOriginal = @{}
    foreach ($taskName in $taskNames) {
        Write-Rt2FixtureFile (Join-Path $taskPayload $taskName) ('new fixture ' + $taskName)
        Write-Rt2FixtureFile (Join-Path $taskMod $taskName) ('original fixture ' + $taskName)
        $taskExpected[$taskName] = Get-Rt2FixtureHash (Join-Path $taskPayload $taskName)
        $taskOriginal[$taskName] = Get-Rt2FixtureHash (Join-Path $taskMod $taskName)
    }
    $taskExpected | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskPackage 'checksums.json') -Encoding UTF8
    $taskBackups = Join-Path $taskFixtureRoot 'Backups'
    Assert-Rt2InstallerThrows { & $taskInstaller -AutoSteamRoots @($taskSteamRoot) -BackupRoot $taskBackups } 'Multiple Slay the Spire 2 installations'
    Remove-Item -LiteralPath (Join-Path $taskSecondGame 'SlayTheSpire2.exe')
    & $taskInstaller -AutoSteamRoots @($taskSteamRoot) -BackupRoot $taskBackups
    Write-Rt2FixtureFile (Join-Path $taskSecondGame 'SlayTheSpire2.exe') 'fixture only; never executed'
    $taskBackup = @(Get-ChildItem -LiteralPath $taskBackups -Directory)[0].FullName
    foreach ($taskName in $taskNames) {
        Assert-Rt2InstallerCheck ((Get-Rt2FixtureHash (Join-Path $taskMod $taskName)) -eq $taskExpected[$taskName]) ('Installed payload ' + $taskName)
        Assert-Rt2InstallerCheck ((Get-Rt2FixtureHash (Join-Path $taskBackup $taskName)) -eq $taskOriginal[$taskName]) ('Backed up original ' + $taskName)
    }
    Write-Rt2FixtureFile (Join-Path $taskPayload 'RolltheSpire2.dll') 'tampered package'
    Assert-Rt2InstallerThrows { & $taskInstaller -GameRoot $taskGameA -BackupRoot $taskBackups } 'Package checksum mismatch'
    Assert-Rt2InstallerCheck ((Get-Rt2FixtureHash (Join-Path $taskMod 'RolltheSpire2.dll')) -eq $taskExpected['RolltheSpire2.dll']) 'Bad package cannot overwrite installed files'
    Write-Rt2FixtureFile (Join-Path $taskPayload 'RolltheSpire2.dll') 'new fixture RolltheSpire2.dll'
    $taskSavedOriginal = [IO.File]::ReadAllBytes((Join-Path $taskBackup 'RolltheSpire2.dll'))
    Write-Rt2FixtureFile (Join-Path $taskBackup 'RolltheSpire2.dll') 'tampered backup'
    Assert-Rt2InstallerThrows { & $taskInstaller -GameRoot $taskGameA -RestoreFrom $taskBackup } 'Backup checksum mismatch'
    Assert-Rt2InstallerCheck ((Get-Rt2FixtureHash (Join-Path $taskMod 'RolltheSpire2.dll')) -eq $taskExpected['RolltheSpire2.dll']) 'Bad backup cannot partially restore files'
    [IO.File]::WriteAllBytes((Join-Path $taskBackup 'RolltheSpire2.dll'), $taskSavedOriginal)
    Assert-Rt2InstallerThrows { & $taskInstaller -GameRoot $taskSecondGame -RestoreFrom $taskBackup } 'Backup target does not match'
    & $taskInstaller -GameRoot $taskGameA -RestoreFrom $taskBackup
    foreach ($taskName in $taskNames) {
        Assert-Rt2InstallerCheck ((Get-Rt2FixtureHash (Join-Path $taskMod $taskName)) -eq $taskOriginal[$taskName]) ('Restored original ' + $taskName)
    }
    # Restore also removes package files that did not exist before installation.
    & $taskInstaller -GameRoot $taskSecondGame -BackupRoot $taskBackups
    $taskFreshMod = Join-Path $taskSecondGame 'mods\RolltheSpire2'
    $taskFreshBackup = @(Get-ChildItem -LiteralPath $taskBackups -Directory | Where-Object FullName -ne $taskBackup)[0].FullName
    & $taskInstaller -GameRoot $taskSecondGame -RestoreFrom $taskFreshBackup
    foreach ($taskName in $taskNames) {
        Assert-Rt2InstallerCheck (-not (Test-Path -LiteralPath (Join-Path $taskFreshMod $taskName))) ('Fresh restore removes ' + $taskName)
    }
    $taskFixtureProcessState.Running = $true
    $taskFixtureProcessState.Executable = Join-Path $taskGameA 'SlayTheSpire2.exe'
    Assert-Rt2InstallerThrows { & $taskInstaller -GameRoot $taskGameA -BackupRoot $taskBackups } 'Close Slay the Spire 2'
    $taskFixtureProcessState.Running = $false
    $taskWorkshopMod = Join-Path $taskLibraryA 'steamapps\workshop\content\2868840\3755166888\RolltheSpire2'
    Write-Rt2FixtureFile (Join-Path $taskWorkshopMod 'RolltheSpire2.json') 'duplicate fixture'
    Assert-Rt2InstallerThrows { & $taskInstaller -GameRoot $taskGameA -BackupRoot $taskBackups } 'Two RolltheSpire2 installations'
    Write-Host ('Passed ' + $script:taskInstallerCheckCount + ' Steam discovery and installer guard checks. Real game files were not used.')
}
finally {
    # Remove only the exact temporary fixture root created above.
    $taskResolvedFixture = [IO.Path]::GetFullPath($taskFixtureRoot)
    $taskExpectedTempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\rt2-steam-installer-'
    if (-not $taskResolvedFixture.StartsWith($taskExpectedTempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing cleanup outside the installer fixture directory.'
    }
    Remove-Item -LiteralPath $taskResolvedFixture -Recurse -Force
}
