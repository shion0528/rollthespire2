param([string]$Destination = '')
$ErrorActionPreference = 'Stop'
$taskRepo = Split-Path -Parent $PSScriptRoot
$taskVersion = '1.3.5-resource.17'
if (-not $Destination) { $Destination = Join-Path (Split-Path -Parent $taskRepo) "RolltheSpire2-performance-$taskVersion" }
$taskPackage = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $taskPackage) { throw 'Choose a new package directory; existing packages are never overwritten.' }
$taskArchiveParent = Split-Path -Parent $taskPackage
$taskModArchive = Join-Path $taskArchiveParent "RolltheSpire2-mod-$taskVersion.zip"
$taskSourceArchive = Join-Path $taskArchiveParent "RolltheSpire2-source-$taskVersion.zip"
foreach ($taskArchive in @($taskModArchive, $taskSourceArchive)) {
    if (Test-Path -LiteralPath $taskArchive) { throw "Choose new archive paths; existing archives are never overwritten: $taskArchive" }
}
$taskBinary = Join-Path $taskRepo '.godot\mono\temp\bin\Release\RolltheSpire2.dll'
if (-not (Test-Path -LiteralPath $taskBinary)) { throw 'Build Release with -p:Rt2ModsDir= before packaging.' }
$taskVersionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($taskBinary)
if ($taskVersionInfo.FileVersion -ne '1.3.5.17') { throw 'Unexpected binary version.' }
$taskManifest = Get-Content -LiteralPath (Join-Path $taskRepo 'RolltheSpire2.json') -Raw | ConvertFrom-Json
if ($taskManifest.version -ne $taskVersion) { throw 'Binary and manifest package versions differ.' }
$taskPayload = Join-Path $taskPackage 'RolltheSpire2'
[void][IO.Directory]::CreateDirectory($taskPayload)
Copy-Item -LiteralPath $taskBinary -Destination (Join-Path $taskPayload 'RolltheSpire2.dll')
Copy-Item -LiteralPath (Join-Path $taskRepo 'RolltheSpire2.json') -Destination (Join-Path $taskPayload 'RolltheSpire2.json')
Copy-Item -LiteralPath (Join-Path $taskRepo 'LICENSE') -Destination (Join-Path $taskPackage 'LICENSE')
Copy-Item -LiteralPath (Join-Path $taskRepo 'USAGE.zh-CN.txt') -Destination (Join-Path $taskPackage '使用说明.txt')Copy-Item -LiteralPath (Join-Path $taskRepo 'PERFORMANCE-CHANGES.txt') -Destination (Join-Path $taskPackage '版本说明.txt')
Copy-Item -LiteralPath (Join-Path $taskRepo 'COMPATIBILITY.txt') -Destination (Join-Path $taskPackage '兼容性说明.txt')
$taskChecksums = @{}
foreach ($taskName in @('RolltheSpire2.dll', 'RolltheSpire2.json')) {
    $taskChecksums[$taskName] = (Get-FileHash -LiteralPath (Join-Path $taskPayload $taskName) -Algorithm SHA256).Hash
}
$taskChecksums | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskPackage 'checksums.json') -Encoding UTF8

# The corresponding source includes modified and new files, without Git history,
# caches, build outputs, or the game's proprietary reference assemblies.
Push-Location $taskRepo
try { $taskSources = @(git -c core.quotePath=false ls-files --cached --others --exclude-standard) }
finally { Pop-Location }
if ($LASTEXITCODE -ne 0 -or $taskSources.Count -lt 10) { throw 'Cannot enumerate the source tree.' }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
# The playable archive is exactly the two-file Mod. Documentation/checksums and
# the independently distributed corresponding source remain outside that ZIP.
$taskModZip = [IO.Compression.ZipFile]::Open($taskModArchive, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($taskName in @('RolltheSpire2.dll', 'RolltheSpire2.json')) {
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskModZip,
            (Join-Path $taskPayload $taskName), "RolltheSpire2/$taskName", [IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $taskModZip.Dispose() }
$taskSourceZip = [IO.Compression.ZipFile]::Open($taskSourceArchive, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($taskRelative in $taskSources | Sort-Object -Unique) {
        $taskSource = [IO.Path]::GetFullPath((Join-Path $taskRepo $taskRelative))
        if (-not $taskSource.StartsWith($taskRepo.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Source entry escaped the repository.'
        }
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskSourceZip, $taskSource,
            $taskRelative.Replace('\', '/'), [IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $taskSourceZip.Dispose() }
Write-Host "Package prepared: $taskPackage"
Write-Host "Pure Mod archive: $taskModArchive"
Write-Host "Independent source archive: $taskSourceArchive"
Write-Host "Source entries: $($taskSources.Count)"
