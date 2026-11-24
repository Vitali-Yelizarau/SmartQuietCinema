#Requires -Version 5.1
<#
.SYNOPSIS
Verifies both packages and runs an experimental endpoint, live PCM, or explicitly targeted control check.
.DESCRIPTION
Use only in the designated VMware guest after a successful driver installation. The script never performs a PnP action.
Supply both archive digests and the driver signer thumbprint from the independent handoff records.
.PARAMETER ControlEndpointId
Exact active MMDevice ID of the guest's existing High Definition Audio Device, explicitly selected for the Control check.
.PARAMETER EvidenceDirectory
Unused directory for packet CSV and independent native stdout and stderr. Defaults to a new sibling of the probe ZIP.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Endpoint', 'Pcm', 'Control')][string]$Check,
    [Parameter(Mandatory)][string]$ProbeArchive,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-fA-F]{64}\z')][string]$ExpectedProbeSha256,
    [Parameter(Mandatory)][string]$DriverPackage,
    [Parameter(Mandatory)][string]$DriverArchive,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-fA-F]{64}\z')][string]$ExpectedDriverSha256,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-fA-F]{40}\z')][string]$ExpectedSignerThumbprint,
    [Parameter(Mandatory)][string]$DevGenPath,
    [Parameter(Mandatory)][string]$SignToolPath,
    [string]$ControlEndpointId,
    [string]$EvidenceDirectory
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

function ConvertTo-NativeArgument {
    <# .SYNOPSIS
    Quotes a Windows native command-line argument, including literal quotes and trailing backslashes.
    #>
    param([AllowEmptyString()][string]$Argument)

    return '"' + ($Argument -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
}

function Invoke-LabProcess {
    <# .SYNOPSIS
    Runs a child with separate stdout and stderr files so Windows PowerShell 5.1 never converts benign stderr into an early terminating error.
    #>
    param([string]$Program, [string[]]$Arguments, [string]$Evidence, [string]$Label)

    $stdout = Join-Path $Evidence "$Label.stdout.txt"
    $stderr = Join-Path $Evidence "$Label.stderr.txt"
    $argumentLine = ($Arguments | ForEach-Object { ConvertTo-NativeArgument $_ }) -join ' '
    $process = Start-Process -FilePath $Program -ArgumentList $argumentLine -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru -Wait -WindowStyle Hidden
    try {
        return @{ ExitCode = $process.ExitCode; StdOut = @(Get-Content -LiteralPath $stdout -Encoding OEM); StdErrPath = $stderr }
    } finally { $process.Dispose() }
}

if ($Check -eq 'Control' -and [string]::IsNullOrWhiteSpace($ControlEndpointId)) { throw 'Select the existing VMware High Definition Audio Device with -ControlEndpointId.' }
if ($Check -ne 'Control' -and $ControlEndpointId) { throw '-ControlEndpointId is only valid for the Control check.' }

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
$archiveDirectory = (Get-Item -LiteralPath $ProbeArchive).DirectoryName
if (!$EvidenceDirectory) { $EvidenceDirectory = Join-Path $archiveDirectory ('PcmEvidence-' + [guid]::NewGuid().ToString('N')) }
$evidence = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($EvidenceDirectory)
foreach ($root in $PSScriptRoot, (Get-Item -LiteralPath $DriverPackage).FullName) {
    $prefix = $root.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ($evidence.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or $evidence.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Keep evidence outside both verified extractions.'
    }
}
if ((Test-Path -LiteralPath $evidence) -or ![IO.Directory]::Exists([IO.Path]::GetDirectoryName($evidence))) { throw 'Supply a new evidence directory with an existing parent.' }
[void](New-Item -ItemType Directory -Path $evidence)
Write-Output "Evidence: $evidence"
$ps51 = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$driverArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $verifyDriver, '-RequireDriverLab', '-DevGenPath', $DevGenPath,
    '-SignToolPath', $SignToolPath, '-ExpectedSignerThumbprint', $ExpectedSignerThumbprint, '-TransferArchivePath', $DriverArchive, '-ExpectedTransferSha256', $ExpectedDriverSha256)
$driver = Invoke-LabProcess $ps51 $driverArgs $evidence 'driver-validation'
$driver.StdOut | Write-Output
if ($driver.ExitCode -ne 0) { throw "Original signed driver package verification failed ($($driver.ExitCode)). Stderr: $($driver.StdErrPath)" }

$probe = Join-Path $PSScriptRoot $entryPoint
$selfTest = Invoke-LabProcess $probe @('self-test') $evidence 'self-test'
$selfTest.StdOut | Write-Output
if ($selfTest.ExitCode -ne 0) { throw "Updated probe self-test failed ($($selfTest.ExitCode)). Stderr: $($selfTest.StdErrPath)" }
$before = Invoke-LabProcess $probe @('defaults') $evidence 'defaults-before'
if ($before.ExitCode -ne 0) { throw "Could not read guest playback defaults ($($before.ExitCode)). Stderr: $($before.StdErrPath)" }
$inventory = Invoke-LabProcess $probe @('list', '--diagnostics') $evidence 'endpoint-list'
$inventory.StdOut | Write-Output
if ($inventory.ExitCode -ne 0) { throw "Could not list active render endpoints ($($inventory.ExitCode)). Stderr: $($inventory.StdErrPath)" }
$experimental = @($inventory.StdOut | Where-Object { $_ -cmatch '\A[^\t]+\t[^\t]*\t\[SmartQuietCinema experiment\]\z' })
if ($experimental.Count -ne 1) { throw "Expected exactly one hardware-verified experimental endpoint, found $($experimental.Count)." }
$endpointId = ($experimental[0] -split "`t", 2)[0]
Write-Output "Verified experimental endpoint: $endpointId"
$command = 'probe'
$label = 'experimental'
if ($Check -eq 'Control') {
    $other = @($inventory.StdOut | Where-Object { $_ -cmatch '\A[^\t]+\t[^\t]*\t\[other render endpoint\]\z' -and ($_ -split "`t", 2)[0] -ieq $ControlEndpointId })
    if ($other.Count -ne 1 -or $endpointId -ieq $ControlEndpointId) { throw 'The selected control must be a separate active render endpoint.' }
    $endpointId = ($other[0] -split "`t", 2)[0]
    $command = 'control'
    $label = 'control'
    Write-Output "Operator-selected diagnostic control: $($other[0])"
}

$failedRates = @()
if ($Check -ne 'Endpoint') {
    $sourceRatesHz = 48000, 44100
    foreach ($rate in $sourceRatesHz) {
        $trace = Join-Path $evidence "$label-$rate-packets.csv"
        $result = Invoke-LabProcess $probe @($command, $endpointId, "$rate", '--trace', $trace) $evidence "$label-$rate"
        $result.StdOut | Write-Output
        Write-Output "$label source $rate Hz exit code: $($result.ExitCode). Stderr: $($result.StdErrPath)"
        if ($result.ExitCode -ne 0) { $failedRates += $rate }
    }
}
$after = Invoke-LabProcess $probe @('defaults') $evidence 'defaults-after'
if ($after.ExitCode -ne 0 -or ($before.StdOut -join "`n") -cne ($after.StdOut -join "`n")) { throw 'The guest playback default snapshot changed or could not be read.' }
$after.StdOut | Write-Output
if ($failedRates.Count) { throw "$label PCM validation failed at source rate(s): $($failedRates -join ', ') Hz. Evidence: $evidence" }
