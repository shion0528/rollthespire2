param(
    [string]$GameRoot = '',
    [string]$RestoreFrom = '',
    [string]$BackupRoot = '',
    [string[]]$AutoSteamRoots
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'SteamGameDiscovery.ps1')

$taskGameRoot = if ($PSBoundParameters.ContainsKey('AutoSteamRoots')) {
    Resolve-Rt2GameRoot -GameRoot $GameRoot -SteamRoots $AutoSteamRoots
} else { Resolve-Rt2GameRoot -GameRoot $GameRoot }
$taskGameExecutable = Join-Path $taskGameRoot 'SlayTheSpire2.exe'
$taskActiveGame = Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -eq $taskGameExecutable } catch { $true }
}
if ($taskActiveGame) { throw 'Close Slay the Spire 2 before installing or restoring this build.' }

$taskSteamApps = Split-Path -Parent (Split-Path -Parent $taskGameRoot)
$taskLocal = Join-Path $taskGameRoot 'mods\RolltheSpire2'
$taskWorkshop = Join-Path $taskSteamApps 'workshop\content\2868840\3755166888\RolltheSpire2'
$taskTargets = @($taskLocal, $taskWorkshop | Where-Object {
    Test-Path -LiteralPath (Join-Path $_ 'RolltheSpire2.json')
})
if ($taskTargets.Count -gt 1) { throw 'Two RolltheSpire2 installations were found. Remove the duplicate before installing.' }
$taskTarget = if ($taskTargets.Count -eq 1) { $taskTargets[0] } else { $taskLocal }
$taskTarget = [IO.Path]::GetFullPath($taskTarget)
$taskNames = @('RolltheSpire2.dll', 'RolltheSpire2.json')

if ($RestoreFrom) {
    $taskBackup = [IO.Path]::GetFullPath($RestoreFrom)
    $taskReceipt = Get-Content -LiteralPath (Join-Path $taskBackup 'receipt.json') -Raw | ConvertFrom-Json
    if ([IO.Path]::GetFullPath($taskReceipt.Target) -ne $taskTarget) {
        throw 'Backup target does not match the installed mod directory.'
    }
    foreach ($taskName in $taskNames) {
        $taskEntry = @($taskReceipt.Files | Where-Object Name -eq $taskName)
        if ($taskEntry.Count -ne 1) { throw "Invalid backup receipt: $taskName" }
        $taskSavedFile = Join-Path $taskBackup $taskName
        if ($taskEntry[0].Existed) {
            if ((Get-FileHash -LiteralPath $taskSavedFile -Algorithm SHA256).Hash -ne $taskEntry[0].Sha256) {
                throw "Backup checksum mismatch: $taskName"
            }
        }
    }
    foreach ($taskName in $taskNames) {
        $taskEntry = @($taskReceipt.Files | Where-Object Name -eq $taskName)[0]
        $taskDestination = Join-Path $taskTarget $taskName
        if ($taskEntry.Existed) { Copy-Item -LiteralPath (Join-Path $taskBackup $taskName) -Destination $taskDestination -Force }
        elseif (Test-Path -LiteralPath $taskDestination) { Remove-Item -LiteralPath $taskDestination }
    }
    Write-Host "Original files restored: $taskTarget"
    return
}

$taskPayload = Join-Path $PSScriptRoot 'RolltheSpire2'
$taskExpected = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'checksums.json') -Raw | ConvertFrom-Json
foreach ($taskName in $taskNames) {
    $taskSource = Join-Path $taskPayload $taskName
    if ((Get-FileHash -LiteralPath $taskSource -Algorithm SHA256).Hash -ne $taskExpected.$taskName) {
        throw "Package checksum mismatch: $taskName"
    }
}
$taskBackupRoot = if ($BackupRoot) { [IO.Path]::GetFullPath($BackupRoot) }
    else { Join-Path $env:APPDATA 'SlayTheSpire2\RolltheSpire2\backups' }
$taskBackup = Join-Path $taskBackupRoot ('resources-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
[void][IO.Directory]::CreateDirectory($taskBackup)
$taskEntries = foreach ($taskName in $taskNames) {
    $taskOriginal = Join-Path $taskTarget $taskName
    $taskExists = Test-Path -LiteralPath $taskOriginal
    $taskHash = ''
    if ($taskExists) {
        $taskHash = (Get-FileHash -LiteralPath $taskOriginal -Algorithm SHA256).Hash
        Copy-Item -LiteralPath $taskOriginal -Destination (Join-Path $taskBackup $taskName)
    }
    [pscustomobject]@{ Name = $taskName; Existed = $taskExists; Sha256 = $taskHash }
}
[pscustomobject]@{ Target = $taskTarget; Files = @($taskEntries); Version = '1.3.5-resource.10' } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $taskBackup 'receipt.json') -Encoding UTF8
[void][IO.Directory]::CreateDirectory($taskTarget)
try {
    foreach ($taskName in $taskNames) {
        $taskDestination = Join-Path $taskTarget $taskName
        Copy-Item -LiteralPath (Join-Path $taskPayload $taskName) -Destination $taskDestination -Force
        if ((Get-FileHash -LiteralPath $taskDestination -Algorithm SHA256).Hash -ne $taskExpected.$taskName) {
            throw "Installed checksum mismatch: $taskName"
        }
    }
}
catch {
    foreach ($taskEntry in $taskEntries) {
        $taskDestination = Join-Path $taskTarget $taskEntry.Name
        if ($taskEntry.Existed) { Copy-Item -LiteralPath (Join-Path $taskBackup $taskEntry.Name) -Destination $taskDestination -Force }
        elseif (Test-Path -LiteralPath $taskDestination) { Remove-Item -LiteralPath $taskDestination }
    }
    throw
}
Write-Host "Installed 1.3.5-resource.10: $taskTarget"
Write-Host "Backup: $taskBackup"
Write-Host 'Start the game, open RT2 Settings, then choose CPU / GPU budgets or Maximum performance.'
Write-Host 'A Workshop update can replace these modified files; reapply this package if needed.'
