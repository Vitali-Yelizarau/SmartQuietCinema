#Requires -Version 5.1
<#
.SYNOPSIS
Verifies an extracted lab bundle and optionally collects read-only guest diagnostics.
.PARAMETER PayloadOnly
Checks hashes and runs the probe self-test without querying system state.
.PARAMETER DevGenPath
Path to DevGen from the guest's authorized Windows Driver Kit.
.PARAMETER SignToolPath
Path to SignTool from the guest's authorized Windows SDK or EWDK.
.PARAMETER RequireDriverLab
Fails unless the guest has the tools and trust needed to verify the signed payload.
.PARAMETER ExpectedSignerThumbprint
Thumbprint obtained independently from the signer. Required for a pending guest-verification bundle.
.PARAMETER TransferArchivePath
Guest-local copy of the signed transfer ZIP. Required for pending guest verification.
.PARAMETER ExpectedTransferSha256
ZIP SHA-256 obtained independently from the signer. Required for pending guest verification.
.PARAMETER EvidenceDirectory
New output directory for guest diagnostics. Omit to print the verification summary only.
#>
[CmdletBinding()]
param(
    [switch]$PayloadOnly,
    [string]$DevGenPath,
    [string]$SignToolPath,
    [switch]$RequireDriverLab,
    [ValidatePattern('\A[0-9A-Fa-f]{40}\z')][string]$ExpectedSignerThumbprint,
    [string]$TransferArchivePath,
    [ValidatePattern('\A[0-9A-Fa-f]{64}\z')][string]$ExpectedTransferSha256,
    [string]$EvidenceDirectory,
    [string]$ProbeResultFile,
    [string]$PnpResultFile
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Resolve-GuestTool {
    <# .SYNOPSIS
    Resolves an external SDK tool for a guest prerequisite check, without installing or invoking it.
    #>
    param([string]$Name, [string]$Path)

    if (!$Path) {
        $tool = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($tool) { $Path = $tool.Path }
    }
    if (!$Path) { return }
    $file = Get-Item -LiteralPath $Path -ErrorAction SilentlyContinue
    if ($file -and !$file.PSIsContainer -and $file.Name -eq $Name) { return $file.FullName }
}

function Save-GuestEvidence {
    <# .SYNOPSIS
    Captures the exact adapter identity, audio and security state, event logs, and operator results for this guest run.
    #>
    param([string]$Destination, [string]$HardwareId)

    $output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination)
    if (Test-Path -LiteralPath $output) { throw "The evidence path already exists: $output" }
    New-Item -ItemType Directory -Path $output | Out-Null
    $state = Join-Path $output 'guest-state.txt'
    $env:COMPUTERNAME | Set-Content -LiteralPath $state
    Get-CimInstance Win32_OperatingSystem | Select-Object Caption, OSArchitecture, BuildNumber | Out-File -LiteralPath $state -Append
    Get-CimInstance Win32_ComputerSystem | Select-Object Manufacturer, Model, HypervisorPresent | Out-File -LiteralPath $state -Append
    Get-NetAdapter -ErrorAction Continue | Select-Object Name, Status, InterfaceDescription | Out-File -LiteralPath $state -Append
    Get-Service AudioEndpointBuilder, Audiosrv | Out-File -LiteralPath $state -Append
    "Secure Boot: $(Confirm-SecureBootUEFI)" | Out-File -LiteralPath $state -Append
    Get-CimInstance -Namespace root\Microsoft\Windows\DeviceGuard -ClassName Win32_DeviceGuard -ErrorAction Continue |
        Select-Object VirtualizationBasedSecurityStatus, SecurityServicesConfigured, SecurityServicesRunning |
        Out-File -LiteralPath $state -Append
    & bcdedit.exe /enum '{current}' | Out-File -LiteralPath $state -Append
    & sc.exe query SmartQuietCinemaVirtualAudio | Out-File -LiteralPath $state -Append
    & pnputil.exe /enum-devices /deviceid $HardwareId /drivers /services | Set-Content -LiteralPath (Join-Path $output 'device.txt')
    & pnputil.exe /enum-drivers | Set-Content -LiteralPath (Join-Path $output 'driver-store.txt')
    $setupLog = Join-Path $env:SystemRoot 'INF\setupapi.dev.log'
    if (Test-Path -LiteralPath $setupLog) { Copy-Item -LiteralPath $setupLog -Destination $output }
    else { Write-Warning 'SetupAPI device log was not found.' }
    $audioLogs = @(Get-WinEvent -ListLog 'Microsoft-Windows-Audio*' -ErrorAction SilentlyContinue | Where-Object IsEnabled | Select-Object -ExpandProperty LogName)
    $logs = @('System', 'Microsoft-Windows-CodeIntegrity/Operational', 'Microsoft-Windows-Kernel-PnP/Configuration') + $audioLogs
    foreach ($log in $logs) {
        $outputName = $log.Replace('/', '_') + '.evtx'
        & wevtutil.exe epl $log (Join-Path $output $outputName)
        if ($LASTEXITCODE -ne 0) { Write-Warning "Could not export $log." }
    }
    if ($ProbeResultFile) { [IO.File]::Copy((Get-Item -LiteralPath $ProbeResultFile).FullName, (Join-Path $output 'probe-results.txt')) }
    if ($PnpResultFile) { [IO.File]::Copy((Get-Item -LiteralPath $PnpResultFile).FullName, (Join-Path $output 'pnp-results.txt')) }
}

function Assert-DriverLabManifest {
    <#
    .SYNOPSIS
    Checks signed-package provenance and requires the independent signer identity for a deferred VMware transfer.
    #>
    param($Manifest, [string]$ExpectedThumbprint)

    if ($Manifest.workingTreeClean -isnot [bool] -or !$Manifest.workingTreeClean -or
        $Manifest.revision -notmatch '\A[0-9a-f]{40,64}\z' -or $Manifest.revision -ne $Manifest.driver.smartQuietCinemaRevision) {
        throw 'The manifest does not identify a signed bundle from a clean revision.'
    }
    if ($Manifest.stageA.signature -eq 'SignedPendingGuestVerification') {
        if ($Manifest.driver.signingMode -ne 'SignedPendingGuestVerification' -or
            $Manifest.stageA.installability -ne 'NotInstallableUntilGuestVerification' -or
            $Manifest.target -notmatch 'VMware Workstation Pro$') {
            throw 'Inconsistent guest-verification manifest status.'
        }
        if (!$ExpectedThumbprint) { throw 'Supply the independently recorded signer with -ExpectedSignerThumbprint.' }
    } elseif ($Manifest.stageA.signature -ne 'TestSignedAuthenticodeVerifiedOnHost' -or
        $Manifest.driver.signingMode -ne 'TestSignedAuthenticodeVerified' -or
        $Manifest.stageA.installability -ne 'RequiresGuestPrerequisites') {
        throw 'The manifest does not identify a supported signed bundle.'
    }
    if ($Manifest.stageA.publicCertificate -cne 'Certificate/SmartQuietCinema-Lab.cer' -or
        $Manifest.driver.certificateThumbprint -notmatch '\A[0-9a-fA-F]{40}\z' -or
        ($ExpectedThumbprint -and $ExpectedThumbprint -ne $Manifest.driver.certificateThumbprint)) {
        throw 'The manifest does not match the expected signer identity.'
    }
}

function Assert-GuestTransferArchive {
    <#
    .SYNOPSIS
    Anchors the extracted manifest and its payload hashes to the independently recorded signed ZIP.
    #>
    param([string]$ArchivePath, [string]$ExpectedHash, [string]$ManifestPath)

    if (!$ArchivePath -or !$ExpectedHash) { throw 'Supply the transfer ZIP and independent SHA-256 with -TransferArchivePath and -ExpectedTransferSha256.' }
    $archiveFile = Get-Item -LiteralPath $ArchivePath
    $stream = [IO.File]::Open($archiveFile.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if ((Get-FileHash -InputStream $stream -Algorithm SHA256).Hash -ne $ExpectedHash) { throw 'The transfer ZIP does not match the expected SHA-256.' }
        $stream.Position = 0
        Add-Type -AssemblyName System.IO.Compression
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $true)
        try {
            $entry = $archive.GetEntry('manifest.json')
            if (!$entry) { throw 'The transfer ZIP has no manifest.' }
            $manifestStream = $entry.Open()
            try {
                if ((Get-FileHash -InputStream $manifestStream -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $ManifestPath -Algorithm SHA256).Hash) {
                    throw 'The extracted manifest does not match the transfer ZIP.'
                }
            } finally { $manifestStream.Dispose() }
        } finally { $archive.Dispose() }
    } finally { $stream.Dispose() }
}

function Assert-GuestDriverSignatures {
    <#
    .SYNOPSIS
    Requires trusted signatures from the selected signer and successful SignTool verification of both catalog members.
    #>
    param([string]$SignTool, [string]$Inf, [string]$Sys, [string]$Cat, [string]$Thumbprint)

    foreach ($file in $Sys, $Cat) {
        $signature = Get-AuthenticodeSignature -LiteralPath $file
        if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $Thumbprint) {
            throw "Guest trust or certificate mismatch: $file"
        }
        & $SignTool verify /pa /v $file
        if ($LASTEXITCODE -ne 0) { throw "Guest SignTool verification failed: $file" }
    }
    foreach ($file in $Inf, $Sys) {
        & $SignTool verify /pa /v /c $Cat $file
        if ($LASTEXITCODE -ne 0) { throw "Guest catalog verification failed: $file" }
    }
}

try {
    if ($PayloadOnly -and ($RequireDriverLab -or $EvidenceDirectory)) { throw '-PayloadOnly cannot be combined with guest diagnostics or installation prerequisites.' }
    $root = Split-Path $PSScriptRoot -Parent
    $manifestPath = Join-Path $root 'manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.revision -ne $manifest.driver.smartQuietCinemaRevision) { throw 'Invalid bundle provenance.' }

    $files = @{}
    foreach ($record in $manifest.files) {
        $path = $record.path
        if ($path -notmatch '\A[^/:\\]+(?:/[^/:\\]+)*\z' -or $path -match '(?:^|/)\.{1,2}(?:/|$)|\.(pfx|p12|pvk|pem|key|snk|pdb)$') {
            throw "Disallowed manifest path: $path"
        }
        $files.Add($path, $record)
        $resolved = Join-Path $root $path.Replace('/', '\')
        if (![IO.File]::Exists($resolved)) { throw "Missing payload file: $path" }
        $file = Get-Item -LiteralPath $resolved
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $file.Length -ne $record.bytes -or
            (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $record.sha256) { throw "Payload hash or length mismatch: $path" }
    }
    foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse -Force) {
        $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
        if ($relative -ne 'manifest.json' -and !$files.ContainsKey($relative)) { throw "Unlisted payload file: $relative" }
    }
    $driverFiles = @('SmartQuietCinemaVirtualAudio.inf', 'SmartQuietCinemaVirtualAudio.sys', 'SmartQuietCinemaVirtualAudio.cat')
    if (@(Get-ChildItem -LiteralPath (Join-Path $root 'Driver') -Force).Count -ne $driverFiles.Count) { throw 'Unexpected Driver directory contents.' }
    foreach ($name in $driverFiles) {
        if (!$files.ContainsKey("Driver/$name") -or $files["Driver/$name"].sha256 -ne $manifest.driver.driverSha256.$name) {
            throw "Native package manifest mismatch: $name"
        }
    }

    $probeName = 'SmartQuietCinema.Tools.VirtualAudioProbe'
    $probePrefix = 'Tools/VirtualAudioProbe/'
    if ($manifest.stageA.probe -ne "$probePrefix$probeName.exe") { throw 'The manifest does not point to the bundled probe.' }
    foreach ($name in @("$probeName.exe", "$probeName.dll", "$probeName.runtimeconfig.json", "$probeName.deps.json", 'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'NAudio.Core.dll', 'NAudio.Wasapi.dll')) {
        if (!$files.ContainsKey("$probePrefix$name")) { throw "Missing probe dependency: $name" }
    }
    $probe = Join-Path $root ($manifest.stageA.probe.Replace('/', '\'))
    & $probe self-test
    if ($LASTEXITCODE -ne 0) { throw 'VirtualAudioProbe self-test failed.' }
    & $probe invalid-command
    if ($LASTEXITCODE -ne 2) { throw 'VirtualAudioProbe did not return its expected CLI usage status.' }
    Write-Output "Verified $($files.Count) payload files and the self-contained probe CLI."
    if ($manifest.stageA.signature -eq 'SignedPendingGuestVerification') {
        Write-Output 'Signed transfer bundle: installation requires successful guest verification with -RequireDriverLab.'
    }

    if (!$PayloadOnly) {
        $devgen = Resolve-GuestTool 'devgen.exe' $DevGenPath
        $signtool = Resolve-GuestTool 'signtool.exe' $SignToolPath
        $issues = @()
        if (!$devgen) { $issues += 'DevGen is missing. Make it available from the guest''s authorized WDK.' }
        if (!$signtool) { $issues += 'SignTool is missing. Make it available from the guest''s authorized SDK.' }
        if ($manifest.stageA.signature -eq 'NotSigned') { $issues += 'The unsigned driver is NotInstallable.' }
        foreach ($issue in $issues) { Write-Warning $issue }
        if ($EvidenceDirectory) { Save-GuestEvidence $EvidenceDirectory $manifest.driver.hardwareId }
        if ($RequireDriverLab) {
            if ($manifest.stageA.signature -eq 'NotSigned') { throw 'Stage A prerequisites are missing.' }
            Assert-DriverLabManifest $manifest $ExpectedSignerThumbprint
            if ($manifest.stageA.signature -eq 'SignedPendingGuestVerification' -or $TransferArchivePath -or $ExpectedTransferSha256) {
                Assert-GuestTransferArchive $TransferArchivePath $ExpectedTransferSha256 $manifestPath
            }
            if ($issues.Count) { throw 'Stage A prerequisites are missing.' }
            $os = Get-CimInstance Win32_OperatingSystem
            $processorArchitectures = @(Get-CimInstance Win32_Processor | Select-Object -ExpandProperty Architecture -Unique)
            $x64Architecture = 9
            if ($processorArchitectures.Count -ne 1 -or $processorArchitectures[0] -ne $x64Architecture -or
                ![Environment]::Is64BitOperatingSystem -or [int]$os.BuildNumber -lt $manifest.driver.minimumWindowsBuild) {
                throw 'Use a supported Windows 11 x64 guest.'
            }
            if (@(Get-Service AudioEndpointBuilder, Audiosrv | Where-Object Status -ne Running).Count) {
                throw 'Windows audio services are not ready.'
            }
            foreach ($tool in $devgen, $signtool) {
                $signature = Get-AuthenticodeSignature -LiteralPath $tool
                if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(?:^|,\s*)O=Microsoft Corporation(?:,|$)') {
                    throw "Guest kit tool lacks a trusted Microsoft signature: $tool"
                }
            }
            $cer = Join-Path $root 'Certificate\SmartQuietCinema-Lab.cer'
            if (!$files.ContainsKey($manifest.stageA.publicCertificate) -or ![IO.File]::Exists($cer) -or
                [Security.Cryptography.X509Certificates.X509Certificate2]::GetCertContentType($cer) -ne 'Cert') {
                throw 'The bundle must contain the selected public certificate.'
            }
            $identity = [Security.Cryptography.X509Certificates.X509Certificate2]::new($cer)
            try {
                $driver = Join-Path $root 'Driver'
                $inf = Join-Path $driver $driverFiles[0]
                $sys = Join-Path $driver $driverFiles[1]
                $cat = Join-Path $driver $driverFiles[2]
                if ($identity.HasPrivateKey -or $identity.Thumbprint -ne $manifest.driver.certificateThumbprint) { throw 'The public certificate does not match the manifest.' }
                Assert-GuestDriverSignatures $signtool $inf $sys $cat $identity.Thumbprint
            } finally { $identity.Dispose() }
            Write-Output 'Stage A files, tools and guest Authenticode trust verified. Confirm the manual VM and kernel-policy prerequisites in the runbook.'
        }
    }
} catch {
    [Console]::Error.WriteLine("Bundle validation failed. $($_.Exception.Message)")
    exit 1
}
