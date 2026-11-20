#Requires -Version 5.1
<#
.SYNOPSIS
Tests an unsigned lab bundle and failure handling without changing Windows or using an audio endpoint.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$BundleDirectory)

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
    $bundle = (Get-Item -LiteralPath $BundleDirectory).FullName
    $parent = [IO.Path]::GetDirectoryName($bundle)
    $work = Join-Path $parent ('.sqc-bundle-tests-' + [guid]::NewGuid().ToString('N'))
    [void](New-Item -ItemType Directory -Path $work)
    $zip = Join-Path $bundle 'SmartQuietCinema-HyperV-Test.zip'
    $digest = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    if ($digest -ne (Get-Content -LiteralPath "$zip.sha256" -Raw).Trim().Split(' ')[0]) { throw 'The ZIP and SHA-256 sidecar disagree.' }
    Write-Output 'PASS Archive SHA-256 sidecar'

    $extract = Join-Path $work 'extract'
    Expand-Archive -LiteralPath $zip -DestinationPath $extract
    $manifest = Get-Content -LiteralPath (Join-Path $extract 'manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.stageA.signature -ne 'NotSigned' -or $manifest.stageA.installability -ne 'NotInstallable' -or $manifest.stageA.publicCertificate) {
        throw 'The review bundle must be noninstallable and contain no signing identity.'
    }
    Write-Output 'PASS Unsigned status without certificate material'

    $ps51 = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $validator = Join-Path $extract 'Scripts\Test-HyperVTestBundle.ps1'
    $validate = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $validator)
    $good = Invoke-TestProcess $ps51 ($validate + '-PayloadOnly') 0
    if ($good -notmatch 'Synthetic tests: [1-9]\d* passed, 0 failed, 0 skipped\.' -or $good -notmatch 'Verified \d+ payload files') {
        throw 'The extracted probe or ZIP inventory did not pass its smoke test.'
    }
    Write-Output 'PASS Extracted inventory, exact driver set and PCM/CLI smoke test'

    $missingTools = Invoke-TestProcess $ps51 ($validate + @('-RequireDriverLab', '-DevGenPath', (Join-Path $work 'devgen.exe'),
        '-SignToolPath', (Join-Path $work 'signtool.exe'))) 1
    if ($missingTools -notmatch 'DevGen is missing' -or $missingTools -notmatch 'NotInstallable') { throw 'Missing guest prerequisites were not diagnosed.' }
    Write-Output 'PASS Missing DevGen and unsigned installation refusal'

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
        Write-Output 'Bundle integration: 10 passed, 0 failed, 0 skipped.'
    } else {
        Write-Output 'Bundle integration: 9 passed, 0 failed, 1 skipped (optional Test Bench unavailable).'
    }
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
} finally {
    if ($work -and [IO.Path]::GetDirectoryName($work) -eq $parent -and [IO.Path]::GetFileName($work) -match '^\.sqc-bundle-tests-[a-f0-9]{32}$') {
        [IO.Directory]::Delete($work, $true)
    }
}
