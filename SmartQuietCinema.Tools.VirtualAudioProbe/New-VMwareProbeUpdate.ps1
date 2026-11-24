#Requires -Version 5.1
<#
.SYNOPSIS
Publishes a self-contained Windows x64 PCM diagnostics ZIP without building or copying a driver.
.PARAMETER OutputDirectory
An unused directory under an existing parent. The archive and checksum are published together.
.PARAMETER NuGetSource
Optional source for restoring existing dependencies, such as the host's local NuGet package cache.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory, [string]$NuGetSource)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $PSScriptRoot 'SmartQuietCinema.Tools.VirtualAudioProbe.csproj'
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$parent = [IO.Path]::GetDirectoryName($output)
if (![IO.Directory]::Exists($parent) -or (Test-Path -LiteralPath $output)) { throw 'Supply a new output directory under an existing parent.' }
$revision = git -C $repo rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $revision -notmatch '\A[0-9a-f]{40,64}\z') { throw 'Unable to read Git provenance.' }
$changes = git -C $repo status --porcelain --untracked-files=normal
if ($LASTEXITCODE -ne 0 -or $changes) { throw 'The update requires a clean Git checkout.' }

$archiveName = 'SmartQuietCinema-VMware-PCM-Diagnostics-win-x64.zip'
$entryPoint = 'Probe/SmartQuietCinema.Tools.VirtualAudioProbe.exe'
$work = Join-Path $parent ('.sqc-probe-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($work)
try {
    $payload = Join-Path $work 'payload'
    $probeDir = Join-Path $payload 'Probe'
    $publish = Join-Path $work 'publish'
    [void][IO.Directory]::CreateDirectory($publish)
    $publishArguments = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--output', $probeDir,
        '--artifacts-path', (Join-Path $work 'build'), '-p:DebugType=None', '-p:DebugSymbols=false')
    if ($NuGetSource) { $publishArguments += @('--source', $NuGetSource) }
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) { throw 'Probe publish failed.' }
    & (Join-Path $payload $entryPoint) self-test
    if ($LASTEXITCODE -ne 0) { throw 'Published probe failed its self-test.' }
    [IO.File]::Copy((Join-Path $PSScriptRoot 'Invoke-VMwareProbeUpdate.ps1'), (Join-Path $payload 'Invoke-VMwareProbeUpdate.ps1'))
    $files = @(foreach ($file in (Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName)) {
        $path = $file.FullName.Substring($payload.Length + 1).Replace('\', '/')
        if ($file.Extension -in '.pdb', '.pfx', '.cer', '.sys', '.cat', '.inf') { throw "Unexpected file in user-mode update: $path" }
        [ordered]@{ path = $path; bytes = $file.Length; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
    })
    $manifestPath = Join-Path $payload 'manifest.json'
    [ordered]@{ schemaVersion = 1; package = 'SmartQuietCinema VMware Probe Update'; revision = $revision; runtime = 'win-x64'; entryPoint = $entryPoint; files = $files } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    $changes = git -C $repo status --porcelain --untracked-files=normal
    if ($LASTEXITCODE -ne 0 -or $changes -or (git -C $repo rev-parse HEAD) -ne $revision) { throw 'The Git checkout changed while publishing the probe.' }

    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $zipPath = Join-Path $publish $archiveName
    $records = $files + @(@{ path = 'manifest.json'; bytes = (Get-Item -LiteralPath $manifestPath).Length; sha256 = (Get-FileHash -LiteralPath $manifestPath).Hash })
    $zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($record in $records) {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $payload $record.path), $record.path, [IO.Compression.CompressionLevel]::Fastest)
        }
    } finally { $zip.Dispose() }
    $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        if ($zip.Entries.Count -ne $records.Count) { throw 'Unexpected probe ZIP inventory.' }
        foreach ($record in $records) {
            $entry = $zip.GetEntry($record.path)
            if (!$entry -or $entry.Length -ne $record.bytes) { throw "Invalid ZIP entry: $($record.path)" }
            $stream = $entry.Open()
            try { if ((Get-FileHash -InputStream $stream -Algorithm SHA256).Hash -ne $record.sha256) { throw "ZIP hash mismatch: $($record.path)" } }
            finally { $stream.Dispose() }
        }
    } finally { $zip.Dispose() }
    $sha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    "$sha256  $archiveName" | Set-Content -LiteralPath "$zipPath.sha256" -Encoding Ascii
    [IO.File]::Copy($manifestPath, (Join-Path $publish 'manifest.json'))
    $transferName = 'Copy-VMwarePcmProbeUpdate.ps1'
    $transferPath = Join-Path $publish $transferName
    [IO.File]::Copy((Join-Path $PSScriptRoot $transferName), $transferPath)
    $transferHash = (Get-FileHash -LiteralPath $transferPath -Algorithm SHA256).Hash
    "$transferHash  $transferName" | Set-Content -LiteralPath "$transferPath.sha256" -Encoding Ascii
    [IO.Directory]::Move($publish, $output)
    Write-Output "Probe update: $(Join-Path $output $archiveName)"
    Write-Output "SHA-256: $sha256"
} finally {
    if ([IO.Path]::GetDirectoryName($work) -eq $parent -and [IO.Path]::GetFileName($work) -match '^\.sqc-probe-[0-9a-f]{32}$') {
        [IO.Directory]::Delete($work, $true)
    }
}
