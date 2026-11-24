#Requires -Version 5.1
<#
.SYNOPSIS
Tests an unsigned review or signed VMware transfer bundle without changing Windows or using an audio endpoint.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$BundleDirectory, [switch]$VMware, [switch]$PendingGuestVerification,
    [ValidatePattern('\A[0-9A-Fa-f]{40}\z')][string]$ExpectedSignerThumbprint)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-TestProcess {
    <# .SYNOPSIS
    Captures a child process and verifies its expected exit code without opening an interactive window.
    #>
    param([string]$Program, [string[]]$Arguments, [int]$ExpectedExit)

    $stdOut = Join-Path $work 'stdout.txt'
    $stdErr = Join-Path $work 'stderr.txt'
    $quotedArguments = ($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $process = Start-Process -FilePath $Program -ArgumentList $quotedArguments -RedirectStandardOutput $stdOut -RedirectStandardError $stdErr -PassThru -Wait -WindowStyle Hidden
    $report = [IO.File]::ReadAllText($stdOut) + [IO.File]::ReadAllText($stdErr)
    if ($process.ExitCode -ne $ExpectedExit) { throw "Expected exit code $ExpectedExit, received $($process.ExitCode). $report" }
    return $report
}

function New-ReferenceWave {
    <# .SYNOPSIS
    Writes original stereo float32 PCM to verify that the extracted Test Bench can actually process a WAV.
    #>
    param([string]$Path)

    $rate = 48000
    $frames = 4800
    $channels = 2
    $bytesPerFrame = $channels * 4
    $payloadBytes = $frames * $bytesPerFrame
    $writer = [IO.BinaryWriter]::new([IO.File]::Open($Path, [IO.FileMode]::CreateNew))
    try {
        $writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF'))
        $writer.Write([int](36 + $payloadBytes))
        $writer.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt '))
        $writer.Write([int]16)
        $writer.Write([int16]3)
        $writer.Write([int16]$channels)
        $writer.Write([int]$rate)
        $writer.Write([int]($rate * $bytesPerFrame))
        $writer.Write([int16]$bytesPerFrame)
        $writer.Write([int16]32)
        $writer.Write([Text.Encoding]::ASCII.GetBytes('data'))
        $writer.Write([int]$payloadBytes)
        for ($frame = 0; $frame -lt $frames; $frame++) {
            $value = [single](0.08 * [Math]::Sin(2 * [Math]::PI * 688 * $frame / $rate))
            $writer.Write($value)
            $writer.Write([single](-0.5 * $value))
        }
    } finally { $writer.Dispose() }
}

$work = $null
try {
    if ($PendingGuestVerification -and (!$VMware -or !$ExpectedSignerThumbprint)) {
        throw 'A pending transfer test requires -VMware and the independently known -ExpectedSignerThumbprint.'
    }
    $bundle = (Get-Item -LiteralPath $BundleDirectory).FullName
    $parent = [IO.Path]::GetDirectoryName($bundle)
    $work = Join-Path $parent ('.sqc-bundle-tests-' + [guid]::NewGuid().ToString('N'))
    [void](New-Item -ItemType Directory -Path $work)
    $zipName = if ($PendingGuestVerification) { 'SmartQuietCinema-VMware-Test-GuestVerification.zip' }
    elseif ($VMware) { 'SmartQuietCinema-VMware-Test-Unsigned.zip' } else { 'SmartQuietCinema-HyperV-Test.zip' }
    $zip = Join-Path $bundle $zipName
    $digest = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    if ($digest -ne (Get-Content -LiteralPath "$zip.sha256" -Raw).Trim().Split(' ')[0]) { throw 'The ZIP and SHA-256 sidecar disagree.' }
    Write-Output 'PASS Archive SHA-256 sidecar'

    $extract = Join-Path $work 'extract'
    Expand-Archive -LiteralPath $zip -DestinationPath $extract
    $manifest = Get-Content -LiteralPath (Join-Path $extract 'manifest.json') -Raw | ConvertFrom-Json
    $lab = if ($VMware) { 'VMware Workstation Pro' } else { 'Hyper-V Generation 2' }
    if (!$manifest.target.EndsWith($lab, [StringComparison]::Ordinal)) { throw "Expected the manifest target to identify $lab." }
    if ($PendingGuestVerification) {
        if ($manifest.stageA.signature -ne 'SignedPendingGuestVerification' -or $manifest.stageA.installability -ne 'NotInstallableUntilGuestVerification' -or
            $manifest.driver.signingMode -ne $manifest.stageA.signature -or $manifest.driver.certificateThumbprint -ne $ExpectedSignerThumbprint -or
            $manifest.workingTreeClean -ne $true -or $manifest.revision -ne $manifest.driver.smartQuietCinemaRevision -or
            $manifest.stageA.publicCertificate -cne 'Certificate/SmartQuietCinema-Lab.cer') { throw 'The pending status, provenance or expected signing identity is incorrect.' }
        $cer = Join-Path $extract 'Certificate\SmartQuietCinema-Lab.cer'
        if ([Security.Cryptography.X509Certificates.X509Certificate2]::GetCertContentType($cer) -ne 'Cert') { throw 'Expected a public CER.' }
        $identity = [Security.Cryptography.X509Certificates.X509Certificate2]::new($cer)
        try {
            if ($identity.HasPrivateKey -or $identity.Thumbprint -ne $ExpectedSignerThumbprint) { throw 'The CER does not match the expected public signer.' }
        } finally { $identity.Dispose() }
        foreach ($name in 'SmartQuietCinemaVirtualAudio.sys', 'SmartQuietCinemaVirtualAudio.cat') {
            $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path (Join-Path $extract 'Driver') $name)
            if (!$signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $ExpectedSignerThumbprint) { throw "Unexpected signer on $name." }
        }
        Write-Output 'PASS Pending status, clean provenance, public CER and observed SYS/CAT signer'
    } else {
        if ($manifest.stageA.signature -ne 'NotSigned' -or $manifest.stageA.installability -ne 'NotInstallable' -or $manifest.stageA.publicCertificate) {
            throw 'The review bundle must be noninstallable and contain no signing identity.'
        }
        Write-Output 'PASS Unsigned status without certificate material'
    }

    $ps51 = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $validator = Join-Path $extract 'Scripts\Test-HyperVTestBundle.ps1'
    $validate = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $validator)
    $good = Invoke-TestProcess $ps51 ($validate + '-PayloadOnly') 0
    if ($good -notmatch 'Synthetic tests: [1-9]\d* passed, 0 failed, 0 skipped\.' -or $good -notmatch 'Verified \d+ payload files') {
        throw 'The extracted probe or ZIP inventory did not pass its smoke test.'
    }
    if ($PendingGuestVerification -and ($good -notmatch 'Signed transfer bundle: installation requires successful guest verification' -or
        $good -match 'guest Authenticode trust verified')) { throw 'Payload-only verification misreported installation readiness.' }
    Write-Output 'PASS Extracted inventory, exact driver set and PCM/CLI smoke test'

    $requireLab = $validate + @('-RequireDriverLab', '-DevGenPath', (Join-Path $work 'devgen.exe'), '-SignToolPath', (Join-Path $work 'signtool.exe'))
    $transfer = @()
    if ($PendingGuestVerification) { $transfer = @('-ExpectedSignerThumbprint', $ExpectedSignerThumbprint, '-TransferArchivePath', $zip, '-ExpectedTransferSha256', $digest) }
    $missingTools = Invoke-TestProcess $ps51 ($requireLab + $transfer) 1
    if ($missingTools -notmatch 'DevGen is missing' -or $missingTools -match 'guest Authenticode trust verified' -or
        $missingTools -notmatch 'Stage A prerequisites are missing' -or (!$PendingGuestVerification -and $missingTools -notmatch 'NotInstallable')) {
        throw 'Missing guest prerequisites were not diagnosed.'
    }
    Write-Output 'PASS Missing DevGen prevents readiness'

    $passed = 9
    if ($PendingGuestVerification) {
        $missingArchive = Invoke-TestProcess $ps51 ($requireLab + @('-ExpectedSignerThumbprint', $ExpectedSignerThumbprint)) 1
        if ($missingArchive -notmatch 'Supply the transfer ZIP and independent SHA-256' -or
            $missingArchive -match 'guest Authenticode trust verified') { throw 'The pending bundle accepted a missing transfer digest.' }
        $wrongHash = Invoke-TestProcess $ps51 ($requireLab + @('-ExpectedSignerThumbprint', $ExpectedSignerThumbprint,
            '-TransferArchivePath', $zip, '-ExpectedTransferSha256', ('0' * 64))) 1
        if ($wrongHash -notmatch 'does not match the expected SHA-256' -or $wrongHash -match 'guest Authenticode trust verified') {
            throw 'The pending bundle accepted an incorrect transfer digest.'
        }
        Write-Output 'PASS Missing independent ZIP digest prevents readiness'
        Write-Output 'PASS Wrong independent ZIP digest prevents readiness'

        $extractedManifest = Join-Path $extract 'manifest.json'
        $manifestOriginal = [IO.File]::ReadAllBytes($extractedManifest)
        try {
            [IO.File]::AppendAllText($extractedManifest, [Environment]::NewLine)
            $changedManifest = Invoke-TestProcess $ps51 ($requireLab + $transfer) 1
            if ($changedManifest -notmatch 'The extracted manifest does not match the transfer ZIP' -or
                $changedManifest -match 'guest Authenticode trust verified') { throw 'A changed extracted manifest was accepted.' }
        } finally { [IO.File]::WriteAllBytes($extractedManifest, $manifestOriginal) }
        Write-Output 'PASS Extracted manifest must match the verified ZIP'

        $sys = Join-Path $extract 'Driver\SmartQuietCinemaVirtualAudio.sys'
        $original = [IO.File]::ReadAllBytes($sys)
        $modified = [byte[]]$original.Clone()
        $modified[0] = $modified[0] -bxor 1
        try {
            [IO.File]::WriteAllBytes($sys, $modified)
            $tampered = Invoke-TestProcess $ps51 ($requireLab + $transfer) 1
            if ($tampered -notmatch 'Payload hash or length mismatch: Driver/SmartQuietCinemaVirtualAudio.sys' -or
                $tampered -match 'guest Authenticode trust verified') { throw 'A changed signed payload was accepted.' }
        } finally { [IO.File]::WriteAllBytes($sys, $original) }
        $passed += 4
        Write-Output 'PASS Modified signed driver refuses readiness before guest tool checks'
    }

    $runtime = Join-Path $extract 'Tools\VirtualAudioProbe\hostfxr.dll'
    $hidden = Join-Path $work 'held-runtime.dll'
    [IO.File]::Move($runtime, $hidden)
    try {
        $missingRuntime = Invoke-TestProcess $ps51 ($validate + '-PayloadOnly') 1
        if ($missingRuntime -notmatch 'Missing payload file: Tools/VirtualAudioProbe/hostfxr.dll') { throw 'Missing runtime was not diagnosed.' }
    } finally { [IO.File]::Move($hidden, $runtime) }
    Write-Output 'PASS Missing app-local runtime refusal'

    $fakeKey = Join-Path $extract 'private.pfx'
    [IO.File]::WriteAllText($fakeKey, 'Unlisted test payload')
    try {
        $extra = Invoke-TestProcess $ps51 ($validate + '-PayloadOnly') 1
        if ($extra -notmatch 'Unlisted payload file: private.pfx') { throw 'Unexpected key-like payload was accepted.' }
    } finally { [IO.File]::Delete($fakeKey) }
    Write-Output 'PASS Unlisted key-like file refusal'

    $manifestPath = Join-Path $extract 'manifest.json'
    $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
    $manifest.files[0].path = '../escape.dll'
    try {
        $manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
        $traversal = Invoke-TestProcess $ps51 ($validate + '-PayloadOnly') 1
        if ($traversal -notmatch 'Disallowed manifest path: ../escape.dll') { throw 'A path outside the bundle was accepted.' }
    } finally { [IO.File]::WriteAllBytes($manifestPath, $manifestBytes) }
    Write-Output 'PASS Relative path validation'

    $repo = Split-Path $PSScriptRoot -Parent
    $hostPaths = @($repo, $env:USERPROFILE) | ForEach-Object { [regex]::Escape($_) + '|' + [regex]::Escape($_.Replace('\', '/')) }
    foreach ($file in Get-ChildItem -LiteralPath $extract -File -Recurse | Where-Object { $_.Name -like 'SmartQuietCinema*' -or $_.Extension -in '.ps1', '.md', '.json' }) {
        $bytes = [IO.File]::ReadAllBytes($file.FullName)
        if ([Text.Encoding]::ASCII.GetString($bytes) -match ($hostPaths -join '|') -or
            [Text.Encoding]::Unicode.GetString($bytes) -match ($hostPaths -join '|')) { throw "A build-host path is embedded in $($file.Name)." }
    }
    Write-Output 'PASS No build-host paths in application-owned payload and metadata'

    $builder = Join-Path $repo 'SmartQuietCinema.Driver.VirtualAudio\New-HyperVTestBundle.ps1'
    $collision = Invoke-TestProcess $ps51 @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $builder, '-UnsignedReview',
        '-SourceCheckout', $repo, '-OutputDirectory', $bundle) 1
    if ($collision -notmatch 'output path already exists' -or $digest -ne (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash) {
        throw 'The existing ZIP was not protected.'
    }
    Write-Output 'PASS No-overwrite publication'

    $bench = Join-Path $extract 'AudioTestBench\SmartQuietCinema.Tools.AudioTestBench.exe'
    if ($manifest.stageB.audioTestBench -eq 'Published') {
        $originalWave = Join-Path $work 'SyntheticOriginal.wav'
        $output = Join-Path $work 'Processed.wav'
        New-ReferenceWave $originalWave
        $originalHash = (Get-FileHash -LiteralPath $originalWave -Algorithm SHA256).Hash
        $processed = Invoke-TestProcess $bench @($originalWave, $output, '--interval', 'test:0:0.05') 0
        if ($processed -notmatch '48000 Hz \| 2 channels \| 4800 frames' -or ![IO.File]::Exists($output) -or
            $originalHash -ne (Get-FileHash -LiteralPath $originalWave -Algorithm SHA256).Hash) { throw 'The offline WAV processing check failed.' }
        Write-Output 'PASS Extracted Test Bench processed original float PCM offline'
        Write-Output "Bundle integration: $($passed + 1) passed, 0 failed, 0 skipped."
    } else {
        Write-Output "Bundle integration: $passed passed, 0 failed, 1 skipped (optional Test Bench unavailable)."
    }
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
} finally {
    if ($work -and [IO.Path]::GetDirectoryName($work) -eq $parent -and [IO.Path]::GetFileName($work) -match '^\.sqc-bundle-tests-[a-f0-9]{32}$') {
        [IO.Directory]::Delete($work, $true)
    }
}
