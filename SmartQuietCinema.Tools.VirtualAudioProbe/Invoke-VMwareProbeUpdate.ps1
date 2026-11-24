#Requires -Version 5.1
<#
.SYNOPSIS
Verifies the original signed driver package and the separate probe update, then runs an endpoint or live PCM check.
.DESCRIPTION
Use only in the designated VMware guest after a successful driver installation. The script never performs a PnP action.
Supply both archive digests and the driver signer thumbprint from the independent handoff records.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Endpoint', 'Pcm')][string]$Check,
    [Parameter(Mandatory)][string]$ProbeArchive,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-fA-F]{64}\z')][string]$ExpectedProbeSha256,
    [Parameter(Mandatory)][string]$DriverPackage,
    [Parameter(Mandatory)][string]$DriverArchive,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-fA-F]{64}\z')][string]$ExpectedDriverSha256,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-fA-F]{40}\z')][string]$ExpectedSignerThumbprint,
    [Parameter(Mandatory)][string]$DevGenPath,
    [Parameter(Mandatory)][string]$SignToolPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-ArchiveEntry {
    <# .SYNOPSIS
    Checks a guest-local ZIP against its independent digest and anchors an extracted file to the corresponding ZIP entry.
    #>
    param([string]$ArchivePath, [string]$ExpectedHash, [string]$EntryName, [string]$LocalPath)

    $path = (Get-Item -LiteralPath $ArchivePath).FullName
    Add-Type -AssemblyName System.IO.Compression
    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if ((Get-FileHash -InputStream $stream -Algorithm SHA256).Hash -ne $ExpectedHash) { throw "Archive SHA-256 mismatch: $path" }
        $stream.Position = 0
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $true)
        try {
            $entry = $archive.GetEntry($EntryName)
            if (!$entry) { throw "Archive entry missing: $EntryName" }
            $entryStream = $entry.Open()
            try {
                if ((Get-FileHash -InputStream $entryStream -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $LocalPath -Algorithm SHA256).Hash) {
                    throw "Extracted file differs from the verified archive: $LocalPath"
                }
            } finally { $entryStream.Dispose() }
        } finally { $archive.Dispose() }
    } finally { $stream.Dispose() }
}

$manifestPath = Join-Path $PSScriptRoot 'manifest.json'
Assert-ArchiveEntry $ProbeArchive $ExpectedProbeSha256 'manifest.json' $manifestPath
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$entryPoint = 'Probe/SmartQuietCinema.Tools.VirtualAudioProbe.exe'
if ($manifest.schemaVersion -ne 1 -or $manifest.package -ne 'SmartQuietCinema VMware Probe Update' -or $manifest.runtime -ne 'win-x64' -or
    $manifest.entryPoint -cne $entryPoint -or $manifest.revision -notmatch '\A[0-9a-f]{40,64}\z') { throw 'Unexpected probe update manifest.' }
$listed = @{}
foreach ($record in $manifest.files) {
    $path = $record.path
    if ($path -cne 'Invoke-VMwareProbeUpdate.ps1' -and $path -notmatch '\AProbe/(?:[A-Za-z0-9_.-]+/)*[A-Za-z0-9_.-]+\z' -or
        $path -match '(?:^|/)\.{1,2}(?:/|$)|\.(pdb|pfx|cer|sys|cat|inf)$') { throw "Disallowed probe payload path: $path" }
    $listed.Add($path, $true)
    $file = Get-Item -LiteralPath (Join-Path $PSScriptRoot $path)
    if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $file.Length -ne $record.bytes -or
        (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $record.sha256) { throw "Invalid probe payload: $path" }
}
foreach ($file in (Get-ChildItem -LiteralPath $PSScriptRoot -File -Recurse -Force)) {
    $relative = $file.FullName.Substring($PSScriptRoot.Length + 1).Replace('\', '/')
    if ($relative -ne 'manifest.json' -and !$listed.ContainsKey($relative)) { throw "Unlisted probe payload: $relative" }
}
if (!$listed.ContainsKey($entryPoint)) { throw 'The verified probe executable is missing.' }

$verifyDriver = Join-Path $DriverPackage 'Scripts\Test-HyperVTestBundle.ps1'
Assert-ArchiveEntry $DriverArchive $ExpectedDriverSha256 'Scripts/Test-HyperVTestBundle.ps1' $verifyDriver
$ps51 = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
& $ps51 -NoProfile -ExecutionPolicy Bypass -File $verifyDriver -RequireDriverLab -DevGenPath $DevGenPath -SignToolPath $SignToolPath -ExpectedSignerThumbprint $ExpectedSignerThumbprint -TransferArchivePath $DriverArchive -ExpectedTransferSha256 $ExpectedDriverSha256
if ($LASTEXITCODE -ne 0) { throw 'Original signed driver package verification failed.' }

$probe = Join-Path $PSScriptRoot $entryPoint
& $probe self-test
if ($LASTEXITCODE -ne 0) { throw 'The updated probe failed its self-test.' }
$before = @(& $probe defaults)
if ($LASTEXITCODE -ne 0) { throw 'Could not read the guest playback defaults.' }
$inventory = @(& $probe list --diagnostics)
$listExitCode = $LASTEXITCODE
$inventory | Write-Output
if ($listExitCode -ne 0) { throw 'Could not list active render endpoints.' }
$experimental = @($inventory | Where-Object { $_ -cmatch '\A[^\t]+\t[^\t]*\t\[SmartQuietCinema experiment\]\z' })
if ($experimental.Count -ne 1) { throw "Expected exactly one hardware-verified experimental endpoint, found $($experimental.Count)." }
$endpointId = ($experimental[0] -split "`t", 2)[0]
Write-Output "Verified experimental endpoint: $endpointId"

$failedRates = @()
if ($Check -eq 'Pcm') {
    $sourceRatesHz = 48000, 44100
    foreach ($rate in $sourceRatesHz) {
        & $probe probe $endpointId $rate
        $status = $LASTEXITCODE
        Write-Output "Source $rate Hz exit code: $status"
        if ($status -ne 0) { $failedRates += $rate }
    }
}
$after = @(& $probe defaults)
if ($LASTEXITCODE -ne 0 -or ($before -join "`n") -cne ($after -join "`n")) { throw 'The guest playback default snapshot changed or could not be read.' }
$after | Write-Output
if ($failedRates.Count) { throw "Live PCM validation failed at source rate(s): $($failedRates -join ', ') Hz." }
