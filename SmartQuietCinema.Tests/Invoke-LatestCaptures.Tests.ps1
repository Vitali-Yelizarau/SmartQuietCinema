#Requires -Version 5.1
<# .SYNOPSIS
Exercises capture selection and safe WAV publication with synthetic files in temporary directories.
#>

$ErrorActionPreference = 'Stop'
$wrapper = Join-Path $PSScriptRoot '..\SmartQuietCinema.Tools.AudioTestBench\Invoke-LatestCaptures.ps1'
$shell = (Get-Process -Id $PID).Path
$results = [Collections.Generic.List[bool]]::new()

function Assert-Condition([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Test-Case([string]$Name, [scriptblock]$Body) {
    try {
        & $Body
        $results.Add($true)
        Write-Output "PASS: $Name"
    } catch {
        $results.Add($false)
        [Console]::Error.WriteLine("FAIL: ${Name}: $($_.Exception.Message)")
    }
}

function New-Capture([string]$Root, [string]$Name) {
    return [IO.Directory]::CreateDirectory((Join-Path $Root $Name)).FullName
}

function Write-TestWave([string]$Path, [float]$Sample = 0.01, [switch]$IntegerPcm, [switch]$Extensible, [switch]$Rf64) {
    <# .SYNOPSIS
    Creates a short, unprocessed mono WAV with a known RMS and selectable input header.
    #>
    $sampleRate = 8000
    $frames = 4000
    $bits = if ($IntegerPcm) { 16 } else { 32 }
    $format = if ($IntegerPcm) { 1 } elseif ($Extensible) { 65534 } else { 3 }
    $formatSize = if ($Extensible) { 40 } else { 16 }
    $blockAlign = [int]($bits / 8)
    $dataSize = $frames * $blockAlign
    $ascii = [Text.Encoding]::ASCII
    $writer = [IO.BinaryWriter]::new([IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write))
    try {
        $riffSize = 32 + $formatSize + $dataSize
        $storedDataSize = [uint32]$dataSize
        if ($Rf64) {
            $writer.Write($ascii.GetBytes('RF64'))
            $writer.Write([uint32]::MaxValue)
            $writer.Write($ascii.GetBytes('WAVEds64'))
            $writer.Write([uint32]28)
            $writer.Write([uint64]($riffSize + 36 + 10))
            $writer.Write([uint64]$dataSize)
            $writer.Write([uint64]$frames)
            $writer.Write([uint32]0)
            # An odd-length ancillary chunk requires a pad byte before the next header.
            $writer.Write($ascii.GetBytes('JUNK'))
            $writer.Write([uint32]1)
            $writer.Write([uint16]0)
            $storedDataSize = [uint32]::MaxValue
        } else {
            $writer.Write($ascii.GetBytes('RIFF'))
            $writer.Write([uint32]$riffSize)
            $writer.Write($ascii.GetBytes('WAVE'))
        }
        $writer.Write($ascii.GetBytes('fmt '))
        $writer.Write([uint32]$formatSize)
        $writer.Write([uint16]$format)
        $writer.Write([uint16]1)
        $writer.Write([uint32]$sampleRate)
        $writer.Write([uint32]($sampleRate * $blockAlign))
        $writer.Write([uint16]$blockAlign)
        $writer.Write([uint16]$bits)
        if ($Extensible) {
            $writer.Write([uint16]22)
            $writer.Write([uint16]$bits)
            $writer.Write([uint32]4)
            $writer.Write([guid]::new('00000003-0000-0010-8000-00aa00389b71').ToByteArray())
        }
        $writer.Write($ascii.GetBytes('fact'))
        $writer.Write([uint32]4)
        $writer.Write([uint32]$frames)
        $writer.Write($ascii.GetBytes('data'))
        $writer.Write($storedDataSize)
        for ($frame = 0; $frame -lt $frames; $frame++) {
            if ($IntegerPcm) { $writer.Write([int16]($Sample * 32767)) }
            else { $writer.Write($Sample) }
        }
    } finally {
        $writer.Dispose()
    }
}

function Invoke-Wrapper([string]$Root) {
    <# .SYNOPSIS
    Runs the script in a fresh PowerShell process so its exit status and default root can be verified.
    #>
    $arguments = if ($Root) { @('-CaptureRoot', $Root) } else { @() }
    $ErrorActionPreference = 'Continue'
    $PSNativeCommandUseErrorActionPreference = $false
    $log = @(& $shell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $wrapper @arguments 2>&1)
    return [pscustomobject]@{ Code = $LASTEXITCODE; Text = $log -join "`n" }
}

function Get-NumberedOutput([string]$Directory) {
    Get-ChildItem -LiteralPath $Directory -File -Filter 'ProcessedOutput_*.wav'
}

function Get-StagingDirectory([string]$Directory) {
    Get-ChildItem -LiteralPath $Directory -Directory -Force -Filter '.offline-staging-*'
}

$tempRoot = [IO.DirectoryInfo]::new([IO.Path]::GetTempPath())
$fixture = $tempRoot.CreateSubdirectory('SmartQuietCinema-Wrapper-' + [guid]::NewGuid().ToString('N'))
$originalLocalAppData = $env:LOCALAPPDATA
try {
    $env:LOCALAPPDATA = [IO.Directory]::CreateDirectory((Join-Path $fixture.FullName 'Mock User Profile')).FullName
    $root = Join-Path $env:LOCALAPPDATA 'SmartQuietCinema\Samples'
    $primary = New-Capture $root '20260103_100000_001'
    $regression = New-Capture $root '20260103_100000_000'
    $older = New-Capture $root '20251231_090000_000'
    $invalidDate = New-Capture $root '20260230_120000_000'
    $invalidPattern = New-Capture $root '20260108_120000_000-extra'
    $nested = New-Capture $invalidPattern '20260109_120000_000'
    $primarySource = Join-Path $primary 'Original.wav'
    $regressionSource = Join-Path $regression 'Original.wav'
    Write-TestWave $primarySource 0.01
    Write-TestWave $regressionSource 0.2 -Extensible
    foreach ($capture in $older, $invalidDate, $invalidPattern, $nested) { Write-TestWave (Join-Path $capture 'Original.wav') }
    [IO.Directory]::SetLastWriteTimeUtc($primary, [DateTime]::UtcNow.AddDays(-1))
    [IO.Directory]::SetLastWriteTimeUtc($older, [DateTime]::UtcNow)
    $sourceHashes = @((Get-FileHash -LiteralPath $primarySource).Hash, (Get-FileHash -LiteralPath $regressionSource).Hash)

    $existingFirst = Join-Path $primary 'ProcessedOutput_1.wav'
    $existingThird = Join-Path $primary 'ProcessedOutput_3.wav'
    $existingCaptureOutput = Join-Path $primary 'Processed.wav'
    [IO.File]::WriteAllText($existingFirst, 'first experiment')
    [IO.File]::WriteAllText($existingThird, 'third experiment')
    [IO.File]::WriteAllText($existingCaptureOutput, 'not an original input')
    $primarySecond = Join-Path $primary 'ProcessedOutput_2.wav'
    $regressionFirst = Join-Path $regression 'ProcessedOutput_1.wav'

    Test-Case 'Default root, newest timestamps, and original-only input' {
        $run = Invoke-Wrapper
        Assert-Condition ($run.Code -eq 0) $run.Text
        Assert-Condition ($run.Text.Contains('Capture (primary): 20260103_100000_001')) 'Incorrect primary reference.'
        Assert-Condition ($run.Text.Contains('Capture (regression): 20260103_100000_000')) 'Incorrect regression reference.'
        Assert-Condition ($run.Text.IndexOf("Original Input: $primarySource", [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $run.Text.IndexOf("Original Input: $regressionSource", [StringComparison]::OrdinalIgnoreCase) -ge 0) $run.Text
        Assert-Condition ($run.Text.Contains('Input:  RMS=0.010000, Peak=0.010000') -and $run.Text.Contains('Input:  RMS=0.200000, Peak=0.200000')) 'Input statistics do not match the originals.'
        Assert-Condition ($run.Text.Contains('Duration: 0.500000 s (4000 frames)') -and $run.Text.Contains('Input WAV: Extensible, 32 bits')) 'Duration or extensible format was not reported.'
        foreach ($capture in $older, $invalidDate, $invalidPattern, $nested) {
            Assert-Condition (@(Get-NumberedOutput $capture).Count -eq 0) "Unselected capture was processed: $capture"
        }
    }

    Test-Case 'Lowest independent numbers and existing file collisions' {
        Assert-Condition ([IO.File]::Exists($primarySecond) -and [IO.File]::Exists($regressionFirst)) 'Expected independent numbers 2 and 1.'
        Assert-Condition ([IO.File]::ReadAllText($existingFirst) -eq 'first experiment') 'The first output was overwritten.'
        Assert-Condition ([IO.File]::ReadAllText($existingThird) -eq 'third experiment') 'The third output was overwritten.'
        Assert-Condition ([IO.File]::ReadAllText($existingCaptureOutput) -eq 'not an original input') 'The original capture output changed.'
    }

    Test-Case 'Repeated run with custom root and a directory collision' {
        $primaryHash = (Get-FileHash -LiteralPath $primarySecond).Hash
        $regressionHash = (Get-FileHash -LiteralPath $regressionFirst).Hash
        $collision = New-Capture $regression 'ProcessedOutput_2.wav'
        $run = Invoke-Wrapper $root
        Assert-Condition ($run.Code -eq 0) $run.Text
        $primaryFourth = Join-Path $primary 'ProcessedOutput_4.wav'
        $regressionThird = Join-Path $regression 'ProcessedOutput_3.wav'
        Assert-Condition ($run.Text.IndexOf("Candidate Output: $primaryFourth", [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $run.Text.IndexOf("Candidate Output: $regressionThird", [StringComparison]::OrdinalIgnoreCase) -ge 0) $run.Text
        Assert-Condition ([IO.Directory]::Exists($collision)) 'The colliding directory was removed.'
        Assert-Condition ((Get-FileHash -LiteralPath $primaryFourth).Hash -eq $primaryHash -and (Get-FileHash -LiteralPath $primarySecond).Hash -eq $primaryHash) 'The repeated primary run changed the WAV bytes.'
        Assert-Condition ((Get-FileHash -LiteralPath $regressionThird).Hash -eq $regressionHash -and (Get-FileHash -LiteralPath $regressionFirst).Hash -eq $regressionHash) 'The repeated regression run changed the WAV bytes.'
        Assert-Condition ((Get-FileHash -LiteralPath $primarySource).Hash -eq $sourceHashes[0] -and (Get-FileHash -LiteralPath $regressionSource).Hash -eq $sourceHashes[1]) 'An original changed.'
        Assert-Condition (@(Get-StagingDirectory $primary).Count -eq 0 -and @(Get-StagingDirectory $regression).Count -eq 0) 'Successful runs left staging files.'
    }

    Test-Case 'Missing original does not select an older replacement' {
        $missingRoot = Join-Path $fixture.FullName 'missing-original'
        $missing = New-Capture $missingRoot '20260103_120000_000'
        $valid = New-Capture $missingRoot '20260102_120000_000'
        $backup = New-Capture $missingRoot '20260101_120000_000'
        Write-TestWave (Join-Path $missing 'Processed.wav')
        Write-TestWave (Join-Path $valid 'Original.wav')
        Write-TestWave (Join-Path $backup 'Original.wav')

        $run = Invoke-Wrapper $missingRoot
        Assert-Condition ($run.Code -eq 1 -and $run.Text.Contains('Status: failed (20260103_120000_000)')) $run.Text
        Assert-Condition (@(Get-NumberedOutput $valid).Count -eq 1) 'The other selected original was not processed.'
        Assert-Condition (@(Get-NumberedOutput $missing).Count -eq 0 -and @(Get-NumberedOutput $backup).Count -eq 0) 'A missing original was silently replaced.'
    }

    Test-Case 'Malformed WAV and CLI failure leave no numbered or staging files' {
        $invalidRoot = Join-Path $fixture.FullName 'invalid-audio'
        $malformed = New-Capture $invalidRoot '20260103_120000_000'
        $unsupported = New-Capture $invalidRoot '20260102_120000_000'
        $unsupportedSource = Join-Path $unsupported 'Original.wav'
        [IO.File]::WriteAllText((Join-Path $malformed 'Original.wav'), 'not a WAV')
        Write-TestWave $unsupportedSource -IntegerPcm
        $hash = (Get-FileHash -LiteralPath $unsupportedSource).Hash

        $run = Invoke-Wrapper $invalidRoot
        Assert-Condition ($run.Code -eq 1 -and $run.Text.Contains('Status: failed (20260103_120000_000)')) $run.Text
        Assert-Condition ($run.Text.Contains('Audio Test Bench failed:') -and $run.Text.Contains('Input must be a 32-bit IEEE float WAV')) $run.Text
        foreach ($capture in $malformed, $unsupported) {
            Assert-Condition (@(Get-NumberedOutput $capture).Count -eq 0 -and @(Get-StagingDirectory $capture).Count -eq 0) 'A failed capture left an output or staging directory.'
        }
        Assert-Condition ((Get-FileHash -LiteralPath $unsupportedSource).Hash -eq $hash) 'A failed run changed the input.'
    }

    Test-Case 'RF64 metadata and odd chunk padding are read; truncated audio is rejected' {
        $headerRoot = Join-Path $fixture.FullName 'header-variants'
        $rf64 = New-Capture $headerRoot '20260103_120000_000'
        $truncated = New-Capture $headerRoot '20260102_120000_000'
        $rf64Source = Join-Path $rf64 'Original.wav'
        $truncatedSource = Join-Path $truncated 'Original.wav'
        Write-TestWave $rf64Source 0.125 -Rf64
        Write-TestWave $truncatedSource
        $writer = [IO.BinaryWriter]::new([IO.File]::OpenWrite($truncatedSource))
        try {
            # Leave the data chunk claiming more bytes than the container holds.
            $writer.BaseStream.SetLength($writer.BaseStream.Length - 1)
            $writer.BaseStream.Position = 4
            $writer.Write([uint32]($writer.BaseStream.Length - 8))
        } finally {
            $writer.Dispose()
        }
        $run = Invoke-Wrapper $headerRoot
        Assert-Condition ($run.Code -eq 1 -and $run.Text.Contains("Truncated WAV chunk 'data'")) $run.Text
        Assert-Condition ($run.Text.Contains('RMS=0.125000, Peak=0.125000') -and @(Get-NumberedOutput $rf64).Count -eq 1) $run.Text
        Assert-Condition (@(Get-NumberedOutput $truncated).Count -eq 0 -and @(Get-StagingDirectory $truncated).Count -eq 0) 'Truncated audio left an output or staging directory.'
    }

    Test-Case 'Missing capture root reports an error without creating it' {
        $missingRoot = Join-Path $fixture.FullName 'does-not-exist'
        $run = Invoke-Wrapper $missingRoot
        Assert-Condition ($run.Code -eq 1 -and $run.Text.Contains('Capture root is missing:')) $run.Text
        Assert-Condition (-not [IO.Directory]::Exists($missingRoot)) 'A missing capture root was created.'
    }

    Test-Case 'Fewer than two valid captures reports an error without processing' {
        $sparseRoot = Join-Path $fixture.FullName 'only-one'
        $only = New-Capture $sparseRoot '20260101_120000_000'
        Write-TestWave (Join-Path $only 'Original.wav')
        $run = Invoke-Wrapper $sparseRoot
        Assert-Condition ($run.Code -eq 1 -and $run.Text.Contains('found 1.')) $run.Text
        Assert-Condition (@(Get-NumberedOutput $only).Count -eq 0) 'A partial selection was processed.'
    }
} finally {
    $env:LOCALAPPDATA = $originalLocalAppData
    $resolvedFixture = (Resolve-Path -LiteralPath $fixture.FullName).ProviderPath
    $resolvedTempRoot = (Resolve-Path -LiteralPath $tempRoot.FullName).ProviderPath
    if ([IO.Path]::GetDirectoryName($resolvedFixture) -ne $resolvedTempRoot.TrimEnd('\')) {
        throw "Fixture resolved outside the temporary directory: $resolvedFixture"
    }
    [IO.Directory]::Delete($resolvedFixture, $true)
}

$passed = @($results | Where-Object { $_ }).Count
$failed = $results.Count - $passed
Write-Output "Wrapper tests: $passed passed, $failed failed, 0 skipped."
if ($failed -ne 0) { exit 1 }
exit 0
