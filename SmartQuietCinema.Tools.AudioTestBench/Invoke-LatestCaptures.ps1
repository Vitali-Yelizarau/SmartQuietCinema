#Requires -Version 5.1
<#
.SYNOPSIS
Processes the two newest original Windows A/B captures with the Audio Test Bench.
.PARAMETER CaptureRoot
Overrides the current user's LocalAppData sample directory.
#>
[CmdletBinding()]
param([ValidateNotNullOrEmpty()][string]$CaptureRoot)

$requiredCaptureCount = 2
$ErrorActionPreference = 'Stop'

function Invoke-BenchCommand {
    <# .SYNOPSIS
    Captures native diagnostics so callers can check the exit code even on Windows PowerShell 5.1.
    #>
    $ErrorActionPreference = 'Continue'
    $PSNativeCommandUseErrorActionPreference = $false
    & dotnet @args 2>&1
}

function Get-LatestCapture {
    <# .SYNOPSIS
    Selects the two newest immediate directories by their strictly parsed capture timestamps.
    #>
    param([string]$Root)

    $valid = foreach ($directory in (Get-ChildItem -LiteralPath $Root -Directory -Force)) {
        $timestamp = [DateTime]::MinValue
        if ($directory.Name -cmatch '\A[0-9]{8}_[0-9]{6}_[0-9]{3}\z' -and
            [DateTime]::TryParseExact($directory.Name, 'yyyyMMdd_HHmmss_fff', [cultureinfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::None, [ref]$timestamp)) {
            [pscustomobject]@{ Directory = $directory; Timestamp = $timestamp }
        }
    }
    return $valid | Sort-Object Timestamp -Descending | Select-Object -First $requiredCaptureCount
}

function Get-CaptureWaveInfo {
    <# .SYNOPSIS
    Reads WAV metadata and verifies payload bounds without loading the CLI's .NET assembly.
    #>
    param([string]$Path)

    $fourCcBytes = 4
    $chunkHeaderBytes = 8
    $minimumDs64Bytes = 28
    $baseFormatBytes = 16
    $formatWithExtensionLengthBytes = 18
    $formatWithPartialExtensionLengthBytes = 17
    $pcmFormatTag = 1
    $ieeeFloatFormatTag = 3
    $extensibleFormatTag = 65534

    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream, [Text.Encoding]::ASCII)
    try {
        $container = [Text.Encoding]::ASCII.GetString($reader.ReadBytes($fourCcBytes))
        $riffSize = [decimal]$reader.ReadUInt32()
        if ($container -cnotin 'RIFF', 'RF64' -or [Text.Encoding]::ASCII.GetString($reader.ReadBytes($fourCcBytes)) -cne 'WAVE') {
            throw "Invalid WAV header: $Path"
        }

        # RIFF chunks are word-aligned; skip their padding byte when the payload size is odd.
        $rf64DataSize = $null
        if ($container -ceq 'RF64') {
            $chunk = [Text.Encoding]::ASCII.GetString($reader.ReadBytes($fourCcBytes))
            $size = [long]$reader.ReadUInt32()
            if ($chunk -cne 'ds64' -or $size -lt $minimumDs64Bytes -or $size -gt ($stream.Length - $stream.Position)) {
                throw "Invalid RF64 size chunk: $Path"
            }
            $nextChunk = $stream.Position + $size + ($size % 2)
            $riffSize = [decimal]$reader.ReadUInt64()
            $rf64DataSize = [decimal]$reader.ReadUInt64()
            $stream.Position = $nextChunk
        }

        $riffEnd = $riffSize + $chunkHeaderBytes
        if ($riffEnd -gt $stream.Length) { throw "Truncated WAV container: $Path" }
        $format = $null
        $dataSize = $null
        while ($stream.Position + $chunkHeaderBytes -le $riffEnd) {
            $chunk = [Text.Encoding]::ASCII.GetString($reader.ReadBytes($fourCcBytes))
            $size = [long]$reader.ReadUInt32()
            if ($chunk -ceq 'data' -and $null -ne $rf64DataSize -and $size -eq [uint32]::MaxValue) { $size = $rf64DataSize }
            if ($size -gt ($riffEnd - $stream.Position)) { throw "Truncated WAV chunk '$chunk': $Path" }
            $nextChunk = $stream.Position + $size + ($size % 2)
            if ($chunk -ceq 'fmt ' -and $null -eq $format) {
                if ($size -lt $baseFormatBytes -or $size -eq $formatWithPartialExtensionLengthBytes -or $size -gt ($formatWithExtensionLengthBytes + [uint16]::MaxValue)) { throw "Invalid WAV format chunk: $Path" }
                $format = $reader.ReadBytes([int]$size)
            } elseif ($chunk -ceq 'data' -and $null -eq $dataSize) {
                $dataSize = [long]$size
            }
            if ($null -ne $format -and $null -ne $dataSize) { break }
            $stream.Position = [long]$nextChunk
        }
        if ($null -eq $format -or $null -eq $dataSize) { throw "Missing WAV format or data chunk: $Path" }

        # WAVEFORMATEX byte offsets are fixed by the on-disk layout; changing them reads the wrong fields.
        $tag = [BitConverter]::ToUInt16($format, 0)
        $channels = [BitConverter]::ToUInt16($format, 2)
        $rate = [BitConverter]::ToInt32($format, 4)
        $blockAlign = [BitConverter]::ToUInt16($format, 12)
        $bits = [BitConverter]::ToUInt16($format, 14)
        if ($rate -le 0 -or $channels -le 0 -or $blockAlign -le 0 -or $dataSize % $blockAlign -ne 0) {
            throw "Invalid or incomplete WAV frames: $Path"
        }

        # Preserve extensible format data while treating an omitted and an empty extension as equivalent.
        $extra = if ($format.Length -ge $formatWithExtensionLengthBytes) { [BitConverter]::ToUInt16($format, $baseFormatBytes) } else { 0 }
        $formatSize = if ($extra -gt 0) { $formatWithExtensionLengthBytes + $extra } else { $baseFormatBytes }
        if ($formatSize -gt $format.Length) { throw "Truncated WAV format extension: $Path" }
        $encoding = switch ($tag) { $pcmFormatTag { 'Pcm' } $ieeeFloatFormatTag { 'IeeeFloat' } $extensibleFormatTag { 'Extensible' } default { $tag } }
        $frames = [long]($dataSize / $blockAlign)
        return [pscustomobject]@{
            Signature = [Convert]::ToBase64String($format, 0, $formatSize)
            Encoding = $encoding
            Bits = $bits
            Rate = $rate
            Channels = $channels
            Frames = $frames
            Seconds = $frames / [double]$rate
        }
    } finally {
        $reader.Dispose()
    }
}

function Publish-NumberedOutput {
    <# .SYNOPSIS
    Publishes a complete same-volume staging file at the lowest available number without overwriting.
    #>
    param([string]$TemporaryWave, [string]$Directory)

    $win32ErrorMask = 0xffff
    $errorFileExists = 80
    $errorAlreadyExists = 183

    for ([long]$number = 1; ; $number++) {
        $destination = Join-Path $Directory ('ProcessedOutput_{0}.wav' -f $number)
        try {
            # The two-argument overload is available on .NET Framework and refuses to replace an existing entry.
            [IO.File]::Move($TemporaryWave, $destination)
            return $destination
        } catch {
            $cause = $_.Exception.GetBaseException()
            # Let the atomic move decide whether the name is free, including collisions with concurrent runs.
            if (($cause -is [IO.IOException] -and ($cause.HResult -band $win32ErrorMask) -in $errorFileExists, $errorAlreadyExists) -or
                ([IO.Directory]::Exists($destination) -and $cause -is [UnauthorizedAccessException])) { continue }
            throw
        }
    }
}

function Invoke-Capture {
    <# .SYNOPSIS
    Processes only Original.wav, verifies the source and output, and cleans up this run's staging files.
    #>
    param($Capture, [string]$Role, [string]$Bench)

    $directory = $Capture.Directory.FullName
    $original = Join-Path $directory 'Original.wav'
    $timestamp = $Capture.Timestamp.ToString('yyyy-MM-dd HH:mm:ss.fff', [cultureinfo]::InvariantCulture)
    Write-Output "Capture ($Role): $($Capture.Directory.Name) | $timestamp"
    Write-Output "Original Input: $original"
    $sourceHash = (Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash
    Write-Output "Original SHA-256: $sourceHash"
    $source = Get-CaptureWaveInfo $original
    $seconds = $source.Seconds.ToString('F6', [cultureinfo]::InvariantCulture)
    Write-Output "Input WAV: $($source.Encoding), $($source.Bits) bits, $($source.Rate) Hz, $($source.Channels) channels"
    Write-Output "Duration: $seconds s ($($source.Frames) frames)"

    $staging = Join-Path $directory ('.offline-staging-' + [guid]::NewGuid().ToString('N'))
    if (-not [SmartQuietCinemaCaptureStaging]::CreateDirectoryExclusive($staging)) {
        $cause = [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
        throw [IO.IOException]::new("Cannot create staging directory '$staging': $($cause.Message)", $cause)
    }

    $temporary = Join-Path $staging 'render.wav'
    try {
        $levels = @(Invoke-BenchCommand $Bench $original $temporary)
        if ($LASTEXITCODE -ne 0) { throw "Audio Test Bench failed: $($levels -join ' ')" }
        $export = Get-CaptureWaveInfo $temporary
        if ($export.Signature -cne $source.Signature -or $export.Frames -ne $source.Frames) {
            throw 'The staged output differs from the original WAV format or frame count.'
        }
        if ((Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash -ne $sourceHash) {
            throw 'Original.wav changed during processing; the staged result will not be published.'
        }
        $outputHash = (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash
        $output = Publish-NumberedOutput $temporary $directory
        $levels
        Write-Output 'Verified output format: 32-bit IEEE float WAV, matching input format and duration'
        Write-Output "Candidate Output: $output"
        Write-Output "Output SHA-256: $outputHash"
        Write-Output 'Original SHA-256 unchanged: true'
    } finally {
        [IO.File]::Delete($temporary)
        [IO.Directory]::Delete($staging, $false)
    }
}

try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Invoke-LatestCaptures.ps1 requires Windows.' }
    if (-not $PSBoundParameters.ContainsKey('CaptureRoot')) {
        if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) { throw 'LOCALAPPDATA is not set.' }
        $CaptureRoot = Join-Path $env:LOCALAPPDATA 'SmartQuietCinema\Samples'
    }
    if (-not (Test-Path -LiteralPath $CaptureRoot -PathType Container)) { throw "Capture root is missing: $CaptureRoot" }
    $captures = @(Get-LatestCapture $CaptureRoot)
    if ($captures.Count -ne $requiredCaptureCount) { throw "Expected two valid timestamp capture directories under '$CaptureRoot'; found $($captures.Count)." }

    $project = Join-Path $PSScriptRoot 'SmartQuietCinema.Tools.AudioTestBench.csproj'
    $build = @(Invoke-BenchCommand build $project --configuration Debug --no-restore --nologo --disable-build-servers)
    if ($LASTEXITCODE -ne 0) { throw "Audio Test Bench build failed before processing. Restore the project if needed. $($build -join ' ')" }
    $bin = Join-Path $PSScriptRoot 'bin\Debug\net10.0'
    $bench = Join-Path $bin 'SmartQuietCinema.Tools.AudioTestBench.dll'

    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

/// <summary>Reserves an exclusive staging directory beside a capture for safe cleanup.</summary>
public static class SmartQuietCinemaCaptureStaging
{
    /// <summary>Creates a new directory and fails if any entry already occupies the path.</summary>
    /// <param name="path">The staging path to reserve.</param>
    /// <returns>Whether the current invocation created the directory.</returns>
    public static bool CreateDirectoryExclusive(string path)
    {
        // Use the extended path even when Win32 long-path awareness is disabled for the caller.
        if (!path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path.Substring(2) : @"\\?\" + path;
        return CreateDirectory(path, IntPtr.Zero);
    }

    /// <summary>Creates the native directory atomically and preserves the collision error for the caller.</summary>
    /// <param name="path">The extended Windows staging path.</param>
    /// <param name="securityAttributes">Zero to inherit the parent directory's access rules.</param>
    /// <returns>Whether a new directory was created.</returns>
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, IntPtr securityAttributes);
}
'@

    $failed = $false
    for ($index = 0; $index -lt $captures.Count; $index++) {
        $capture = $captures[$index]
        $role = if ($index -eq 0) { 'primary' } else { 'regression' }
        try {
            Invoke-Capture $capture $role $bench
            Write-Output 'Status: succeeded'
        } catch {
            $failed = $true
            [Console]::Error.WriteLine("Status: failed ($($capture.Directory.Name)): $($_.Exception.Message)")
        }
    }
    if ($failed) { exit 1 }
    exit 0
} catch {
    [Console]::Error.WriteLine("Status: failed: $($_.Exception.GetBaseException().Message)")
    exit 1
}
