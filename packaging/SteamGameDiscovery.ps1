# Steam discovery is read-only and probes only registered roots, fixed common
# locations and libraries named by Steam. It never recursively scans a drive.

function ConvertFrom-Rt2SteamLibraryVdf {
    [CmdletBinding()]
    param([AllowEmptyString()][string]$Content)

    $taskVdfRoot = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    $taskVdfCurrent = $taskVdfRoot
    $taskVdfStack = [Collections.Generic.Stack[object]]::new()
    $taskVdfKey = $null
    $taskVdfTokens = [regex]::Matches($Content, '(?m)//[^\r\n]*|"((?:\\.|[^"\\])*)"|([{}])')
    foreach ($taskVdfToken in $taskVdfTokens) {
        if ($taskVdfToken.Value.StartsWith('//')) { continue }
        if ($taskVdfToken.Groups[1].Success) {
            # Decode only VDF's escaped quote and backslash. Preserve unknown
            # escapes so an older file with a literal D:\new path is not damaged.
            $taskVdfValue = [regex]::Replace($taskVdfToken.Groups[1].Value, '\\([\\"])', '$1')
            if ($null -eq $taskVdfKey) { $taskVdfKey = $taskVdfValue }
            else { $taskVdfCurrent[$taskVdfKey] = $taskVdfValue; $taskVdfKey = $null }
        }
        elseif ($taskVdfToken.Value -eq '{') {
            if ($null -eq $taskVdfKey) { throw 'Invalid Steam library VDF: an object has no key.' }
            $taskVdfChild = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
            $taskVdfCurrent[$taskVdfKey] = $taskVdfChild
            $taskVdfStack.Push($taskVdfCurrent)
            $taskVdfCurrent = $taskVdfChild
            $taskVdfKey = $null
        }
        else {
            if ($null -ne $taskVdfKey -or $taskVdfStack.Count -eq 0) { throw 'Invalid Steam library VDF: unmatched closing brace.' }
            $taskVdfCurrent = $taskVdfStack.Pop()
        }
    }
    if ($null -ne $taskVdfKey -or $taskVdfStack.Count -ne 0) { throw 'Invalid Steam library VDF: incomplete object.' }
    $taskVdfLibraries = $null
    if (-not $taskVdfRoot.TryGetValue('libraryfolders', [ref]$taskVdfLibraries) -or
        $taskVdfLibraries -isnot [Collections.IDictionary]) { return }
    foreach ($taskVdfIndex in $taskVdfLibraries.Keys) {
        if ($taskVdfIndex -notmatch '^\d+$') { continue }
        $taskVdfEntry = $taskVdfLibraries[$taskVdfIndex]
        $taskVdfPath = if ($taskVdfEntry -is [string]) { $taskVdfEntry }
            elseif ($taskVdfEntry -is [Collections.IDictionary]) { $taskVdfEntry['path'] }
            else { $null }
        if ($taskVdfPath -is [string] -and -not [string]::IsNullOrWhiteSpace($taskVdfPath)) { $taskVdfPath }
    }
}

function Get-Rt2NormalizedSteamPath {
    param([AllowEmptyString()][string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    try {
        $taskSteamPath = [Environment]::ExpandEnvironmentVariables($Path.Trim().Trim('"'))
        # Discovered paths must be absolute. A stale or unfamiliar VDF value
        # must not become relative to the package's working directory.
        if ($taskSteamPath -notmatch '^(?:[A-Za-z]:[\\/]|\\\\[^\\]+\\[^\\]+)') { return }
        $taskSteamPath = [IO.Path]::GetFullPath($taskSteamPath)
        if ($taskSteamPath.Length -gt [IO.Path]::GetPathRoot($taskSteamPath).Length) {
            $taskSteamPath = $taskSteamPath.TrimEnd([char[]]@('\', '/'))
        }
        $taskSteamPath
    }
    catch { Write-Verbose ('Ignoring invalid Steam path: ' + $Path) }
}

function Get-Rt2SteamRootCandidates {
    [CmdletBinding()]
    param()
    $taskSteamRoots = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($taskSteamRegistry in @('HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam',
        'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam')) {
        try { $taskSteamRecord = Get-ItemProperty -LiteralPath $taskSteamRegistry -ErrorAction Stop }
        catch { continue }
        foreach ($taskSteamProperty in @('SteamPath', 'InstallPath', 'SteamExe')) {
            $taskSteamValue = $taskSteamRecord.PSObject.Properties[$taskSteamProperty]
            if ($null -eq $taskSteamValue) { continue }
            $taskSteamCandidate = Get-Rt2NormalizedSteamPath ([string]$taskSteamValue.Value)
            if ($taskSteamCandidate -and $taskSteamProperty -eq 'SteamExe') {
                $taskSteamCandidate = [IO.Path]::GetDirectoryName($taskSteamCandidate)
            }
            if ($taskSteamCandidate) { [void]$taskSteamRoots.Add($taskSteamCandidate) }
        }
    }
    foreach ($taskSteamProgramRoot in @(${env:ProgramFiles(x86)}, $env:ProgramW6432, $env:ProgramFiles, $env:LOCALAPPDATA)) {
        if (-not [string]::IsNullOrWhiteSpace($taskSteamProgramRoot)) {
            $taskSteamCandidate = Get-Rt2NormalizedSteamPath (Join-Path $taskSteamProgramRoot 'Steam')
            if ($taskSteamCandidate) { [void]$taskSteamRoots.Add($taskSteamCandidate) }
        }
    }
    # Probe a few conventional names on ready fixed drives, without listing or
    # descending into their directories. Custom roots come from registry/VDF.
    foreach ($taskSteamDrive in [IO.DriveInfo]::GetDrives()) {
        try {
            if ($taskSteamDrive.DriveType -ne [IO.DriveType]::Fixed -or -not $taskSteamDrive.IsReady) { continue }
            foreach ($taskSteamRelative in @('Steam', 'SteamLibrary', 'Games\Steam', 'Program Files (x86)\Steam', 'Program Files\Steam')) {
                [void]$taskSteamRoots.Add((Join-Path $taskSteamDrive.RootDirectory.FullName $taskSteamRelative))
            }
        }
        catch { Write-Verbose ('Ignoring unavailable drive: ' + $taskSteamDrive.Name) }
    }
    foreach ($taskSteamCandidate in $taskSteamRoots) { $taskSteamCandidate }
}

function Find-Rt2SteamGameInstallations {
    [CmdletBinding()]
    param([string[]]$SteamRoots = @(Get-Rt2SteamRootCandidates))
    $taskSteamPending = [Collections.Generic.Queue[string]]::new()
    $taskSteamVisited = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $taskGameInstalls = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($taskSteamRoot in $SteamRoots) {
        $taskSteamCandidate = Get-Rt2NormalizedSteamPath $taskSteamRoot
        if ($taskSteamCandidate) { $taskSteamPending.Enqueue($taskSteamCandidate) }
    }
    while ($taskSteamPending.Count -gt 0) {
        $taskSteamLibrary = $taskSteamPending.Dequeue()
        if (-not $taskSteamVisited.Add($taskSteamLibrary) -or
            -not (Test-Path -LiteralPath $taskSteamLibrary -PathType Container)) { continue }
        $taskGameCandidate = Join-Path $taskSteamLibrary 'steamapps\common\Slay the Spire 2'
        if (Test-Path -LiteralPath (Join-Path $taskGameCandidate 'SlayTheSpire2.exe') -PathType Leaf) {
            [void]$taskGameInstalls.Add([IO.Path]::GetFullPath($taskGameCandidate))
        }
        foreach ($taskSteamVdfRelative in @('steamapps\libraryfolders.vdf', 'config\libraryfolders.vdf')) {
            $taskSteamVdfPath = Join-Path $taskSteamLibrary $taskSteamVdfRelative
            if (-not (Test-Path -LiteralPath $taskSteamVdfPath -PathType Leaf)) { continue }
            try {
                $taskSteamContent = Get-Content -LiteralPath $taskSteamVdfPath -Raw -Encoding UTF8
                foreach ($taskSteamListedRoot in @(ConvertFrom-Rt2SteamLibraryVdf $taskSteamContent)) {
                    $taskSteamCandidate = Get-Rt2NormalizedSteamPath $taskSteamListedRoot
                    if ($taskSteamCandidate) { $taskSteamPending.Enqueue($taskSteamCandidate) }
                }
            }
            catch { Write-Verbose ('Ignoring unreadable Steam library list ' + $taskSteamVdfPath + ': ' + $_.Exception.Message) }
        }
    }
    foreach ($taskGameInstall in $taskGameInstalls) { $taskGameInstall }
}

function Resolve-Rt2GameRoot {
    [CmdletBinding()]
    param([AllowEmptyString()][string]$GameRoot = '', [string[]]$SteamRoots)
    if (-not [string]::IsNullOrWhiteSpace($GameRoot)) {
        $taskExplicitGameRoot = [IO.Path]::GetFullPath($GameRoot.Trim().Trim('"')).TrimEnd([char[]]@('\', '/'))
        if (-not (Test-Path -LiteralPath (Join-Path $taskExplicitGameRoot 'SlayTheSpire2.exe') -PathType Leaf)) {
            throw ('Game installation not found at the explicit -GameRoot: ' + $taskExplicitGameRoot)
        }
        return $taskExplicitGameRoot
    }
    if (-not $PSBoundParameters.ContainsKey('SteamRoots')) { $SteamRoots = @(Get-Rt2SteamRootCandidates) }
    $taskFoundGames = @(Find-Rt2SteamGameInstallations -SteamRoots $SteamRoots | Sort-Object)
    if ($taskFoundGames.Count -eq 1) { return $taskFoundGames[0] }
    if ($taskFoundGames.Count -gt 1) {
        throw ("Multiple Slay the Spire 2 installations were found. Pass -GameRoot with the intended directory:`n - " +
            ($taskFoundGames -join "`n - "))
    }
    throw 'Slay the Spire 2 was not found in registered Steam roots or libraryfolders.vdf. Pass -GameRoot with the directory containing SlayTheSpire2.exe.'
}
