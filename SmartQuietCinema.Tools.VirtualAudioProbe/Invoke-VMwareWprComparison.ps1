#Requires -Version 5.1
<#
.SYNOPSIS
Runs a counterbalanced WPR comparison in the designated VMware guest using its verified native PCM Probe.
.DESCRIPTION
Keeps the signed driver and Probe extractions immutable. Captures timer-resolution snapshots without requesting a resolution, and archives all available evidence even when the existing PCM acceptance checks fail.
#>
[CmdletBinding(DefaultParameterSetName = 'Experiment')]
param(
    [Parameter(ParameterSetName = 'Experiment')][string]$LabRoot = 'C:\Test',
    [Parameter(ParameterSetName = 'Experiment')][string]$DevGenPath,
    [Parameter(ParameterSetName = 'Experiment')][string]$SignToolPath,
    [Parameter(ParameterSetName = 'Experiment')][string]$XperfPath,
    [Parameter(ParameterSetName = 'Experiment')][ValidateRange(1, 3)][int]$Cycles = 1,
    [Parameter(ParameterSetName = 'Experiment')][ValidateSet(0, 2, 15, 60)][int]$CarryoverDelaySeconds = 0,
    [Parameter(ParameterSetName = 'Experiment')][ValidateSet('Sampling', 'Scheduling')][string]$IsolationProfile,
    [Parameter(Mandatory, ParameterSetName = 'Cleanup', DontShow)][int]$OwnerId,
    [Parameter(Mandatory, ParameterSetName = 'Cleanup', DontShow)][string]$CleanupDirectory,
    [Parameter(Mandatory, ParameterSetName = 'Cleanup', DontShow)][string]$CompletionSignal
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Start-LabChild {
    <# .SYNOPSIS
    Starts a Windows child with literal arguments and separate stdout and stderr evidence under PowerShell 5.1.
    #>
    param([string]$Program, [string[]]$Arguments, [string]$LogPrefix, [switch]$Wait)
    $argumentLine = ($Arguments | ForEach-Object { '"' + ($_ -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"' }) -join ' '
    $child = Start-Process -FilePath $Program -ArgumentList $argumentLine -RedirectStandardOutput "$LogPrefix.stdout.txt" -RedirectStandardError "$LogPrefix.stderr.txt" -PassThru -WindowStyle Hidden -Wait:$Wait
    # Keep the process handle open so Windows PowerShell 5.1 records an exit code after an asynchronous wait.
    if (!$Wait) { [void]$child.Handle }
    return $child
}

function Invoke-LabChild {
    <# .SYNOPSIS
    Preserves the native exit code and logs for synchronous guest commands.
    #>
    param([string]$Program, [string[]]$Arguments, [string]$LogPrefix)
    $child = Start-LabChild $Program $Arguments $LogPrefix -Wait
    try {
        if ($null -eq $child.ExitCode) { throw "No exit code was recorded for $Program." }
        [IO.File]::WriteAllText("$LogPrefix.exit.txt", [string]$child.ExitCode)
        $child.ExitCode
    } finally { $child.Dispose() }
}

function Stop-LabTrace {
    <# .SYNOPSIS
    Saves or cancels only the recorded private instance. A failed stop invalidates the trial even if cancellation succeeds.
    #>
    param([string]$Wpr, [string]$Instance, [string]$Directory, [bool]$Started, [switch]$Discard)
    if ($Instance -cnotmatch '\ASqcWpr-[a-f0-9]{32}\z') { throw 'Invalid private WPR instance name.' }
    $trace = Join-Path $Directory 'capture.etl'
    if ($Started -and !$Discard) {
        try {
            $stop = Invoke-LabChild $Wpr @('-stop', $trace, '-instancename', $Instance) (Join-Path $Directory 'wpr-stop')
            if ($stop -eq 0) { return ([IO.File]::Exists($trace) -and (Get-Item -LiteralPath $trace).Length -gt 0) }
        } catch {
            Write-Warning "Could not save WPR instance ${Instance}: $_"
        }
    }
    $cancel = Invoke-LabChild $Wpr @('-cancel', '-instancename', $Instance) (Join-Path $Directory 'wpr-cancel')
    if ($cancel -ne 0) { throw "Cleanup could not be confirmed for private WPR instance $Instance (exit $cancel)." }
    return ($Started -and $Discard)
}

function Assert-RunnerArchive {
    <# .SYNOPSIS
    Anchors the existing verifier itself to the independently pinned Probe archive before executing it.
    #>
    param([string]$Archive, [string]$ExpectedHash, [string]$Runner)
    $stream = [IO.File]::OpenRead($Archive)
    try {
        if ((Get-FileHash -InputStream $stream).Hash -ne $ExpectedHash) { throw "Probe archive SHA-256 mismatch: $Archive" }
        $stream.Position = 0
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $true)
        try {
            $entry = $zip.GetEntry('Invoke-VMwareProbeUpdate.ps1')
            if (!$entry) { throw 'The verified Probe archive is missing its runner.' }
            $content = $entry.Open()
            try {
                if ((Get-FileHash -InputStream $content).Hash -ne (Get-FileHash -LiteralPath $Runner).Hash) { throw 'The extracted Probe runner does not match the archive.' }
            } finally { $content.Dispose() }
        } finally { $zip.Dispose() }
    } finally { $stream.Dispose() }
}

function Initialize-TimerQuery {
    <# .SYNOPSIS
    Loads the read-only native clock resolution query before starting any sample or trace.
    #>
    if ('SmartQuietCinema.LabTimerResolution' -as [type]) { return }
    Add-Type @'
using System.Runtime.InteropServices;

namespace SmartQuietCinema
{
    /// <summary>Reads Windows timer interval limits and the current interval without registering a resolution request.</summary>
    public static class LabTimerResolution
    {
        /// <summary>Queries native timer intervals in 100 ns units and returns the operation's NTSTATUS.</summary>
        [DllImport("ntdll.dll", ExactSpelling = true)]
        public static extern int NtQueryTimerResolution(out uint firstLimit, out uint secondLimit, out uint currentInterval);
    }
}
'@
}

function Read-TimerResolution {
    <# .SYNOPSIS
    Records the system query on the same monotonic clock that the Probe uses to mark streaming boundaries.
    #>
    param([string]$Phase)
    [uint32]$first = 0
    [uint32]$second = 0
    [uint32]$current = 0
    $ticks = [Diagnostics.Stopwatch]::GetTimestamp()
    $status = [SmartQuietCinema.LabTimerResolution]::NtQueryTimerResolution([ref]$first, [ref]$second, [ref]$current)
    $min = [Math]::Min($first, $second)
    $max = [Math]::Max($first, $second)
    [pscustomobject]@{ Phase = $Phase; Utc = [DateTime]::UtcNow.ToString('O'); Ticks = $ticks; FrequencyHz = [Diagnostics.Stopwatch]::Frequency;
        MinInterval100ns = $min; MaxInterval100ns = $max; CurrentInterval100ns = $current; Status = ('0x{0:X8}' -f $status); Valid = ($status -ge 0 -and $min -gt 0 -and $current -ge $min -and $current -le $max) }
}

function Set-StreamingPhases {
    <# .SYNOPSIS
    Labels only samples inside both render streams' play-to-stop window as in-stream measurements.
    #>
    param([object[]]$Samples, [string]$ProbeOutput)
    $pattern = '(?m)^Stopwatch: frequency_hz=(\d+), origin_ticks=(\d+), [^\r\n]+?, second_play_ticks=(\d+), stop_request_ticks=(\d+), joined_ticks=(\d+)\r?$'
    $clock = [regex]::Match($ProbeOutput, $pattern)
    if (!$clock.Success -or [long]$clock.Groups[1].Value -ne [Diagnostics.Stopwatch]::Frequency) { return 0 }
    $origin = [long]$clock.Groups[2].Value
    $play = $origin + [long]$clock.Groups[3].Value
    $stop = $origin + [long]$clock.Groups[4].Value
    $joined = $origin + [long]$clock.Groups[5].Value
    if ($play -ge $stop -or $stop -gt $joined) { return 0 }
    $count = 0
    foreach ($sample in $Samples) {
        if ($sample.Phase -ne 'probe-active') { continue }
        if ($sample.Ticks -lt $play) { $sample.Phase = 'probe-startup' }
        elseif ($sample.Ticks -lt $stop) { $sample.Phase = 'streaming'; if ($sample.Valid) { $count++ } }
        elseif ($sample.Ticks -lt $joined) { $sample.Phase = 'stream-teardown' }
        else { $sample.Phase = 'after-streaming' }
    }
    return $count
}

function Get-TrialSchedule {
    <# .SYNOPSIS
    Generates a Williams design with all conditions in every position and all ordered predecessor pairs within each cycle.
    #>
    param([int]$Count)
    $rows = 'Off Audio Combined CPU', 'Audio CPU Off Combined', 'CPU Combined Audio Off', 'Combined Off CPU Audio'
    for ($cycle = 1; $cycle -le $Count; $cycle++) {
        for ($block = 0; $block -lt $rows.Count; $block++) {
            $order = $rows[$block] -split ' '
            for ($position = 0; $position -lt $order.Count; $position++) {
                [pscustomobject]@{ Cycle = $cycle; Block = $block + 1; Position = $position + 1; Condition = $order[$position] }
            }
        }
    }
}

function Get-FocusedSchedule {
    <# .SYNOPSIS
    Balances two matched pairs in opposite order. Carryover arms share an untraced target and differ only in the primer's WPR state.
    #>
    param([int]$DelaySeconds, [string]$Isolation)
    $reference = if ($Isolation) { 'Reference' } else { 'Off' }
    $candidate = if ($Isolation) { $Isolation } else { 'CPU' }
    $order = $candidate, $reference, $reference, $candidate
    for ($index = 0; $index -lt $order.Count; $index++) {
        $arm = $index + 1
        $condition = $order[$index]
        $role = if ($DelaySeconds) { 'Primer' } else { 'Isolation' }
        [pscustomobject]@{ Cycle = 1; Block = $arm; Position = 1; Condition = $condition; Role = $role; Primer = $condition; DelaySeconds = $DelaySeconds }
        if ($DelaySeconds) {
            [pscustomobject]@{ Cycle = 1; Block = $arm; Position = 2; Condition = 'Off'; Role = 'Target'; Primer = $condition; DelaySeconds = $DelaySeconds }
        }
    }
}

function Write-IsolationProfiles {
    <# .SYNOPSIS
    Writes equivalent file-mode collectors with a shared minimal process/image baseline and one selected kernel event family.
    #>
    param([string]$Path)
    $variants = [ordered]@{ Reference = @(); Sampling = @('SampledProfile'); Scheduling = @('CSwitch', 'ReadyThread') }
    $providers = foreach ($name in $variants.Keys) {
        $keywords = (@('ProcessThread', 'Loader') + $variants[$name] | ForEach-Object { "<Keyword Value=`"$_`" />" }) -join ''
        "    <SystemProvider Id=`"$name`"><Keywords>$keywords</Keywords></SystemProvider>"
    }
    $profiles = foreach ($name in $variants.Keys) {
        "    <Profile Id=`"$name.Verbose.File`" Name=`"$name`" Description=`"SmartQuietCinema $name isolation`" DetailLevel=`"Verbose`" LoggingMode=`"File`">" +
            "<Collectors><SystemCollectorId Value=`"SqcSystem`"><SystemProviderId Value=`"$name`" /></SystemCollectorId></Collectors></Profile>"
    }
    $xml = @('<?xml version="1.0" encoding="utf-8"?>', '<WindowsPerformanceRecorder Version="1.0"><Profiles>',
        '    <SystemCollector Id="SqcSystem" Name="SqcIsolation"><BufferSize Value="1024" /><Buffers Value="20" /></SystemCollector>') +
        @($providers) + @($profiles) + '</Profiles></WindowsPerformanceRecorder>'
    [IO.File]::WriteAllLines($Path, $xml)
}

function Assert-IsolationCapture {
    <# .SYNOPSIS
    Requires the intended live keywords and trace event families, excluding contaminated or lossy isolation trials.
    #>
    param([string]$Condition, [string]$Text, [switch]$TraceStats)
    $expected = [ordered]@{ SampledProfile = ($Condition -eq 'Sampling'); CSwitch = ($Condition -eq 'Scheduling'); ReadyThread = ($Condition -eq 'Scheduling'); Power = $false }
    $patterns = @{ SampledProfile = 'Sampled Profile'; CSwitch = 'Thread: CSwitch'; ReadyThread = 'Thread: ReadyThread'; Power = 'Power: Idle State Change' }
    if ($TraceStats) {
        foreach ($label in 'Events', 'Buffers') {
            if ($Text -notmatch "(?m)^Total # Lost $label\s*:\s*0\s*$") { throw "Isolation trace has missing or nonzero lost $label counters." }
        }
    } else {
        $collectors = [regex]::Matches($Text, '(?m)^\s*Collector Name\s*:\s*([^\r\n]+)')
        if ($collectors.Count -ne 1 -or $collectors[0].Groups[1].Value -notmatch 'SqcIsolation|WPR System Collector' -or
            $Text -notmatch "(?m)^\s*Profile\s*:\s*$Condition\.Verbose\.File\s*$" -or $Text -notmatch '(?m)^\s*System Keywords\s*$' -or
            $Text -notmatch '(?m)^\s*ProcessThread\s*$' -or $Text -notmatch '(?m)^\s*Loader\s*$' -or $Text -match '(?m)^\s*(DPC|Interrupt|System Stacks)\s*$') {
            throw 'WPR did not report a single isolation collector with the expected profile and baseline keywords.'
        }
    }
    foreach ($keyword in $expected.Keys) {
        $pattern = "(?m)^\s*$keyword\s*$"
        if ($TraceStats) { $pattern = '(?m)^\s*0x[0-9a-f]+\s+.*?\s' + $patterns[$keyword] + '\s*$' }
        if (($Text -match $pattern) -ne $expected[$keyword]) { throw "Isolation coverage for $Condition does not match $keyword." }
    }
}

function Wait-TrialStart {
    <# .SYNOPSIS
    Holds the common idle interval or waits on the matched target deadline without changing timer resolution.
    #>
    param([long]$DueTicks, [int]$IdleSeconds)
    if (!$DueTicks) { Start-Sleep -Seconds $IdleSeconds; return }
    $frequency = [Diagnostics.Stopwatch]::Frequency
    $milliseconds = ($DueTicks - [Diagnostics.Stopwatch]::GetTimestamp()) * 1000.0 / $frequency
    if ($milliseconds -gt 0) { Start-Sleep -Milliseconds ([int][Math]::Ceiling($milliseconds)) }
}

function Get-Median {
    <# .SYNOPSIS
    Returns the median of available intervals and leaves an empty population unreported.
    #>
    param([double[]]$Values)
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { return $null }
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2) { return $sorted[$middle] }
    return ($sorted[$middle - 1] + $sorted[$middle]) / 2
}

function Get-PcmMetrics {
    <# .SYNOPSIS
    Summarizes native PCM packets, transport flags and signed deltas, and separates source reads from the first render fill.
    #>
    param([string]$PacketPath, [int]$Rate)
    $invariant = [Globalization.CultureInfo]::InvariantCulture
    $discontinuity = 1
    $timestampError = 4
    $stereoChannels = 2
    $packets = @(Import-Csv -LiteralPath $PacketPath)
    $pcm = @($packets | Where-Object { [long]$_.frames -gt 0 })
    if ($pcm.Count -lt 2) { throw 'At least two PCM packets are required to measure a trial.' }
    $transitions = @($pcm | Select-Object -Skip 1)
    $flags = @($transitions | Where-Object { [Convert]::ToUInt32($_.raw_flags_hex.Substring(2), 16) -band $discontinuity })
    $mismatches = @($transitions | Where-Object { $_.position_delta_frames -ne '' -and [decimal]$_.position_delta_frames -ne 0 })
    $deltaCounts = @($mismatches | Group-Object position_delta_frames | ForEach-Object { "$($_.Name):$($_.Count)" })
    $repeatIntervals = @($transitions | Where-Object { $_.position_frames -eq $_.previous_position_frames -and $_.arrival_delta_us } | ForEach-Object { [double]::Parse($_.arrival_delta_us, $invariant) })
    $advanceIntervals = @($transitions | Where-Object { $_.position_delta_frames -eq '0' -and $_.arrival_delta_us } | ForEach-Object { [double]::Parse($_.arrival_delta_us, $invariant) })
    $intervals = @($transitions | Where-Object arrival_delta_us | ForEach-Object { [double]::Parse($_.arrival_delta_us, $invariant) })
    $last = $pcm.Count - 1
    $elapsed = ([double]::Parse($pcm[$last].arrival_us, $invariant) - [double]::Parse($pcm[0].arrival_us, $invariant)) / 1e6
    if ($elapsed -le 0) { throw 'Packet arrivals have no positive elapsed time.' }
    $native = @(Import-Csv -LiteralPath "$PacketPath.native.csv")
    $invalidCalls = @($native | Where-Object { $_.next_hresult -ne '0x00000000' -or ($_.get_hresult -and $_.get_hresult -ne '0x00000000') -or ($_.release_hresult -and $_.release_hresult -ne '0x00000000') })
    $result = [ordered]@{ PcmPackets = $pcm.Count; EmptyPackets = $packets.Count - $pcm.Count; Discontinuities = $flags.Count; PositionMismatches = $mismatches.Count;
        PositionDeltaCounts = $deltaCounts -join ';'; QpcRegressions = @($transitions | Where-Object { $_.qpc_delta_100ns -ne '' -and [decimal]$_.qpc_delta_100ns -lt 0 }).Count;
        QpcRepeats = @($transitions | Where-Object { $_.qpc_delta_100ns -eq '0' }).Count;
        TimestampErrors = @($pcm | Where-Object { [Convert]::ToUInt32($_.raw_flags_hex.Substring(2), 16) -band $timestampError }).Count;
        NativeNonSuccessRows = $invalidCalls.Count; PcmSeconds = ($pcm | Measure-Object frames -Sum).Sum / $Rate; ArrivalSeconds = $elapsed;
        MedianArrivalUs = Get-Median $intervals; MedianBeforeRepeatUs = Get-Median $repeatIntervals; MedianBeforeOrdinaryAdvanceUs = Get-Median $advanceIntervals }
    foreach ($voice in 'left', 'right') {
        $render = @(Import-Csv -LiteralPath "$PacketPath.render-$voice.csv")
        if ($render.Count -lt 2) { throw "Missing initial fill or refill data for the $voice render voice." }
        $result["${voice}InitialFillSeconds"] = [long]$render[0].written_samples / $stereoChannels / $Rate
        $sourceSeconds = ($render | Select-Object -Skip 1 | Measure-Object written_samples -Sum).Sum / $stereoChannels / $Rate
        $result["${voice}RefillSourceSeconds"] = $sourceSeconds
        $result["${voice}RefillSourcePerArrivalSecond"] = $sourceSeconds / $elapsed
    }
    return $result
}

function Invoke-WprTrial {
    <# .SYNOPSIS
    Runs the identical 48 kHz workload for one condition and durably hands off named cleanup before attempting WPR start.
    #>
    param([string]$Wpr, [string]$Probe, [string]$Endpoint, [object]$Trial, [string]$Directory, [string]$ActiveSession,
        [int]$IdleSeconds = 1, [long]$DueTicks = 0, [switch]$DiscardTrace, [string]$ProfilePath)
    $sourceRate = 48000
    $sampleIntervalMs = 250
    $timeoutMs = 30000
    $deadlineToleranceSeconds = 0.25
    $profiles = @{ Off = @(); Audio = @('Audio'); CPU = @('CPU'); Combined = @('Audio', 'CPU') }
    if ($ProfilePath) { $profiles[$Trial.Condition] = @("$ProfilePath!$($Trial.Condition)") }
    $timer = [Collections.Generic.List[object]]::new()
    $instance = $null
    $child = $null
    $started = $false
    $traceSaved = $null
    $streamSamples = 0
    $probeExit = -1
    $output = ''
    $prefix = Join-Path $Directory 'probe'
    $packetPath = Join-Path $Directory 'packets.csv'
    try {
        Wait-TrialStart $DueTicks $IdleSeconds
        $timer.Add((Read-TimerResolution 'before-condition'))
        if ($DueTicks -and ($timer[0].Ticks - $DueTicks) -gt $deadlineToleranceSeconds * [Diagnostics.Stopwatch]::Frequency) {
            throw 'The OFF target missed its matched launch deadline by more than 250 ms. Inspect primer cancellation and defaults logs.'
        }
        if ($Trial.Condition -ne 'Off') {
            $instance = 'SqcWpr-' + [guid]::NewGuid().ToString('N')
            [IO.File]::WriteAllText((Join-Path $Directory 'wpr-instance.txt'), $instance)
            [IO.File]::WriteAllText($ActiveSession, $instance)
            $startArguments = foreach ($profile in $profiles[$Trial.Condition]) { '-start'; $profile }
            $start = Invoke-LabChild $Wpr @($startArguments + @('-filemode', '-instancename', $instance)) (Join-Path $Directory 'wpr-start')
            if ($start -ne 0) { throw "WPR could not start $($Trial.Condition) (exit $start)." }
            $started = $true
            if ($ProfilePath) {
                $statusPrefix = Join-Path $Directory 'wpr-active'
                $status = Invoke-LabChild $Wpr @('-status', 'profiles', 'collectors', '-instancename', $instance) $statusPrefix
                if ($status -ne 0) { throw "Could not inspect active WPR collectors (exit $status)." }
                Assert-IsolationCapture $Trial.Condition (Get-Content -LiteralPath "$statusPrefix.stdout.txt" -Raw -Encoding OEM)
            }
        }
        $timer.Add((Read-TimerResolution 'after-condition-start'))
        $child = Start-LabChild $Probe @('probe-sequence', $Endpoint, "$sourceRate", '--trace', $packetPath) $prefix
        $deadline = [Diagnostics.Stopwatch]::StartNew()
        while (!$child.WaitForExit($sampleIntervalMs)) {
            if ($deadline.ElapsedMilliseconds -ge $timeoutMs) { throw 'The native Probe exceeded its trial timeout.' }
            $timer.Add((Read-TimerResolution 'probe-active'))
        }
        $timer.Add((Read-TimerResolution 'after-probe-exit'))
        $probeExit = $child.ExitCode
        if ($null -eq $probeExit) { throw 'The Probe did not report a process exit code.' }
        [IO.File]::WriteAllText("$prefix.exit.txt", [string]$probeExit)
    } finally {
        try {
            if ($child) {
                try { if (!$child.HasExited) { $child.Kill(); $child.WaitForExit() } }
                finally { $child.Dispose() }
            }
        } finally {
            try {
                if ($instance) {
                    $traceSaved = Stop-LabTrace $Wpr $instance $Directory $started -Discard:$DiscardTrace
                    [IO.File]::WriteAllText($ActiveSession, '')
                }
            } finally {
                $timer.Add((Read-TimerResolution 'after-condition-stop'))
                if ($probeExit -ge 0) {
                    $output = Get-Content -LiteralPath "$prefix.stdout.txt" -Encoding OEM -Raw
                    $streamSamples = Set-StreamingPhases $timer.ToArray() $output
                }
                $timer | Export-Csv -LiteralPath (Join-Path $Directory 'timer-resolution.csv') -Encoding UTF8 -NoTypeInformation
            }
        }
    }
    if ($instance -and !$traceSaved) { throw "The WPR session did not finish as requested for $($Trial.Condition)." }
    if ($output -notmatch '(?m)^Capture: [^\r\n]*, 48000 Hz, 2 channels,') { throw 'The Probe did not report the expected 48 kHz stereo capture format.' }
    if ($output -notmatch '(?m)^Capture reader: native Windows COM, stimulus=aperiodic,') { throw 'The Probe did not report the expected native aperiodic workload.' }
    if (!$streamSamples -or @($timer | Where-Object { !$_.Valid }).Count) { throw 'Timer query coverage or validity is incomplete. Inspect timer-resolution.csv.' }
    $metrics = Get-PcmMetrics $packetPath $sourceRate
    $firstAfterStream = $timer | Where-Object { $_.Phase -in 'after-streaming', 'after-probe-exit' } | Select-Object -First 1
    $playClock = [regex]::Match($output, '(?m)^Stopwatch: frequency_hz=\d+, origin_ticks=(\d+), [^\r\n]+?, second_play_ticks=(\d+), stop_request_ticks=(\d+),')
    $originTicks = [long]$playClock.Groups[1].Value
    $traceDisposition = if (!$instance) { 'None' } elseif ($DiscardTrace) { 'Discarded' } else { 'Saved' }
    $result = [ordered]@{ Cycle = $Trial.Cycle; Block = $Trial.Block; Position = $Trial.Position; Condition = $Trial.Condition; SourceRateHz = $sourceRate; CaptureRateHz = $sourceRate;
        BeforeConditionTicks = $timer[0].Ticks; AfterProbeExitTicks = $timer[$timer.Count - 2].Ticks; AfterConditionStopTicks = $timer[$timer.Count - 1].Ticks;
        PlaybackTicks = $originTicks + [long]$playClock.Groups[2].Value; StopRequestTicks = $originTicks + [long]$playClock.Groups[3].Value;
        ClockFrequencyHz = [Diagnostics.Stopwatch]::Frequency; TraceDisposition = $traceDisposition;
        ProbeExit = $probeExit;
        TransportPassed = ($output -match '(?m)^Transport: PASS\r?$'); AcceptancePassed = ($probeExit -eq 0); StreamingTimerSamples = $streamSamples;
        TimerBefore100ns = $timer[0].CurrentInterval100ns; TimerStarted100ns = $timer[1].CurrentInterval100ns;
        TimerStreaming100ns = (@($timer | Where-Object Phase -eq 'streaming' | Select-Object -ExpandProperty CurrentInterval100ns -Unique) -join ';');
        TimerFirstAfterStream100ns = $firstAfterStream.CurrentInterval100ns; TimerFirstAfterStreamPhase = $firstAfterStream.Phase;
        TimerAfterProbe100ns = $timer[$timer.Count - 2].CurrentInterval100ns; TimerAfterCondition100ns = $timer[$timer.Count - 1].CurrentInterval100ns }
    foreach ($key in $metrics.Keys) { $result[$key] = $metrics[$key] }
    return [pscustomobject]$result
}

function Invoke-CleanupWatch {
    <# .SYNOPSIS
    Waits on the owning shell without periodic wakeups and retries the persisted private cancellation if the shell exits early.
    #>
    param([int]$ParentId, [string]$Directory, [string]$Signal)
    $owner = [Diagnostics.Process]::GetProcessById($ParentId)
    $done = [Threading.EventWaitHandle]::OpenExisting($Signal)
    $parentExit = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset)
    try {
        $parentExit.SafeWaitHandle = [Microsoft.Win32.SafeHandles.SafeWaitHandle]::new($owner.Handle, $false)
        [IO.File]::WriteAllText((Join-Path $Directory 'watch-ready.txt'), [string]$PID)
        if ([Threading.WaitHandle]::WaitAny(@($done, $parentExit)) -eq 0) { return }
    } finally { $parentExit.Dispose(); $done.Dispose(); $owner.Dispose() }
    $activeSession = Join-Path $Directory 'active-session.txt'
    $instance = [IO.File]::ReadAllText($activeSession)
    if (!$instance) { return }
    if ($instance -cnotmatch '\ASqcWpr-[a-f0-9]{32}\z') { throw 'Cleanup watch refused an invalid private WPR instance name.' }
    $wpr = Join-Path $env:SystemRoot 'System32\wpr.exe'
    # The parent may have exited while wpr.exe was still starting the private session.
    foreach ($attempt in 1..3) {
        Start-Sleep -Seconds 2
        $cancel = Invoke-LabChild $wpr @('-cancel', '-instancename', $instance) (Join-Path $Directory "watch-cancel-$attempt")
        if ($cancel -eq 0) { return }
    }
    throw "Cleanup watch could not confirm cancellation of $instance. See the watch logs."
}

if ($PSCmdlet.ParameterSetName -eq 'Cleanup') { Invoke-CleanupWatch $OwnerId $CleanupDirectory $CompletionSignal; return }
if (($CarryoverDelaySeconds -and $IsolationProfile) -or (($CarryoverDelaySeconds -or $IsolationProfile) -and $Cycles -ne 1)) {
    throw 'Select one focused experiment per invocation. -Cycles applies only to the original matrix.'
}

$computer = Get-CimInstance -ClassName Win32_ComputerSystem
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if ($computer.Manufacturer -notlike 'VMware*' -or $computer.Model -notlike 'VMware*' -or !$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this experiment from elevated Windows PowerShell 5.1 inside the designated VMware guest.'
}

$lab = (Get-Item -LiteralPath $LabRoot).FullName
$probeRoot = Join-Path $lab 'SmartQuietCinema-PCM-Native'
$driverRoot = Join-Path $lab 'SmartQuietCinema-VMware-GuestVerification'
$probeArchive = Join-Path $probeRoot 'SmartQuietCinema-VMware-PCM-Native-win-x64.zip'
$driverArchive = Join-Path $driverRoot 'SmartQuietCinema-VMware-Test-GuestVerification.zip'
$probeHash = '4CAD1D0887B4B2895B7ECF86E8C4F62E3F66F5C71836F9356A84C1F70E99FFE4'
$driverHash = '3063502B790B56D42F890937576D12F98A50BA5BDA8F215C6DB29ADD001CE88F'
$signer = 'F5C7AEDFE2F744588C911CE5EF4E3D5EE719FD4A'
$runner = Join-Path $probeRoot 'Extracted\Invoke-VMwareProbeUpdate.ps1'
$probe = Join-Path $probeRoot 'Extracted\Probe\SmartQuietCinema.Tools.VirtualAudioProbe.exe'
$driver = Join-Path $driverRoot 'Extracted'
$shell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$wpr = Join-Path $env:SystemRoot 'System32\wpr.exe'
$mode = if ($CarryoverDelaySeconds) { "Carryover${CarryoverDelaySeconds}s" } elseif ($IsolationProfile) { "Isolation$IsolationProfile" } else { 'Profiles' }
$evidence = Join-Path $lab ("PcmWpr$mode-" + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N'))
$archive = "$evidence.zip"
$activeSession = Join-Path $evidence 'active-session.txt'
[void][IO.Directory]::CreateDirectory($evidence)
[IO.File]::WriteAllText($activeSession, '')
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$schedule = if ($CarryoverDelaySeconds -or $IsolationProfile) { @(Get-FocusedSchedule $CarryoverDelaySeconds $IsolationProfile) } else { @(Get-TrialSchedule $Cycles) }
$frequency = [Diagnostics.Stopwatch]::Frequency
$focusedIdleSeconds = 60
$primer = $null
$profilePath = ''
$watch = $null
$done = $null
$errorText = $null
$results = [Collections.Generic.List[object]]::new()
try {
    $schedule | Export-Csv -LiteralPath (Join-Path $evidence 'schedule.csv') -Encoding UTF8 -NoTypeInformation
    [pscustomobject]@{ ScriptSha256 = (Get-FileHash -LiteralPath $PSCommandPath).Hash; Wpr = $wpr; WprVersion = (Get-Item -LiteralPath $wpr).VersionInfo.FileVersion;
        ProbeArchive = $probeArchive; ProbeSha256 = $probeHash; DriverArchive = $driverArchive; DriverSha256 = $driverHash; SignerThumbprint = $signer;
        Mode = $mode; CarryoverDelaySeconds = $CarryoverDelaySeconds; IsolationProfile = $IsolationProfile } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'experiment.json') -Encoding UTF8
    Assert-RunnerArchive $probeArchive $probeHash $runner
    $verifyArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $runner, '-Check', 'Endpoint', '-ProbeArchive', $probeArchive,
        '-ExpectedProbeSha256', $probeHash, '-DriverPackage', $driver, '-DriverArchive', $driverArchive, '-ExpectedDriverSha256', $driverHash, '-ExpectedSignerThumbprint', $signer)
    if ($DevGenPath) { $verifyArgs += @('-DevGenPath', $DevGenPath) }
    if ($SignToolPath) { $verifyArgs += @('-SignToolPath', $SignToolPath) }
    $before = Join-Path $evidence 'verification-before'
    $status = Invoke-LabChild $shell ($verifyArgs + @('-EvidenceDirectory', $before)) (Join-Path $evidence 'verification-before')
    if ($status -ne 0) { throw "Original signed package and Probe verification failed (exit $status)." }
    $identity = @(Get-Content -LiteralPath "$before.stdout.txt" -Encoding OEM | Where-Object { $_ -cmatch '^Verified experimental endpoint: [^\s]+$' })
    if ($identity.Count -ne 1) { throw 'Expected one endpoint ID from the hardware-based verifier.' }
    $endpoint = $identity[0].Substring('Verified experimental endpoint: '.Length)
    $defaults = Get-Content -LiteralPath (Join-Path $before 'defaults-before.stdout.txt') -Raw -Encoding OEM
    Initialize-TimerQuery
    $query = Read-TimerResolution 'preflight'
    $query | Export-Csv -LiteralPath (Join-Path $evidence 'timer-preflight.csv') -Encoding UTF8 -NoTypeInformation
    if (!$query.Valid) { throw "The native timer-resolution query failed: $($query.Status)." }
    foreach ($command in 'profiles', 'profint', 'status') {
        $status = Invoke-LabChild $wpr @("-$command") (Join-Path $evidence "wpr-$command")
        if ($status -ne 0) { throw "WPR $command failed (exit $status)." }
    }
    $builtInProfiles = if ($CarryoverDelaySeconds) { @('CPU') } elseif (!$IsolationProfile) { @('Audio', 'CPU', 'Audio+CPU') } else { @() }
    foreach ($profile in $builtInProfiles) {
        $export = Join-Path $evidence "$profile.wprp"
        $status = Invoke-LabChild $wpr @('-exportprofile', $profile, $export, '-filemode') (Join-Path $evidence "wpr-export-$profile")
        if ($status -ne 0 -or ![IO.File]::Exists($export)) { throw "WPR export failed for $profile (exit $status)." }
        $status = Invoke-LabChild $wpr @('-profiledetails', $profile, '-filemode') (Join-Path $evidence "wpr-details-$profile")
        if ($status -ne 0) { throw "WPR profile details failed for $profile (exit $status)." }
    }
    $logman = Join-Path $env:SystemRoot 'System32\logman.exe'
    $status = Invoke-LabChild $logman @('query', '-ets') (Join-Path $evidence 'etl-sessions-before')
    if ($status -ne 0) { throw "Could not inventory existing ETW sessions (exit $status)." }
    if (Select-String -LiteralPath (Join-Path $evidence 'etl-sessions-before.stdout.txt') -Pattern 'WPR_initiated_') {
        throw 'An existing WPR collector would contaminate the Off condition. Its owner must end it before the comparison.'
    }
    if (!$XperfPath) {
        $xperf = Get-Command xperf.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($xperf) { $XperfPath = $xperf.Path }
    }
    if ($XperfPath) { $XperfPath = (Get-Item -LiteralPath $XperfPath).FullName }
    if ($IsolationProfile) {
        if (!$XperfPath) { throw 'Supply xperf.exe through -XperfPath or PATH to verify isolation ETL event coverage.' }
        $profilePath = Join-Path $evidence 'Isolation.wprp'
        if ($profilePath.Contains('!')) { throw 'The isolation profile path cannot contain the WPR profile selector delimiter (!).' }
        Write-IsolationProfiles $profilePath
        foreach ($profile in 'Reference', $IsolationProfile) {
            $status = Invoke-LabChild $wpr @('-profiledetails', "$profilePath!$profile", '-filemode') (Join-Path $evidence "wpr-details-$profile")
            if ($status -ne 0) { throw "WPR could not load the $profile isolation profile (exit $status)." }
            Assert-IsolationCapture $profile (Get-Content -LiteralPath (Join-Path $evidence "wpr-details-$profile.stdout.txt") -Raw -Encoding OEM)
        }
    }

    $signal = 'Local\SqcWprDone-' + [guid]::NewGuid().ToString('N')
    $done = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, $signal)
    $watchArgs = @('-NoProfile', '-Mta', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath, '-OwnerId', "$PID", '-CleanupDirectory', $evidence, '-CompletionSignal', $signal)
    $watch = Start-LabChild $shell $watchArgs (Join-Path $evidence 'watch')
    $ready = Join-Path $evidence 'watch-ready.txt'
    for ($attempt = 0; $attempt -lt 50 -and ![IO.File]::Exists($ready) -and !$watch.HasExited; $attempt++) { Start-Sleep -Milliseconds 100 }
    if (![IO.File]::Exists($ready) -or $watch.HasExited) { throw 'Private WPR cleanup watch did not start.' }

    foreach ($trial in $schedule) {
        if ($watch.HasExited) { throw 'Private WPR cleanup watch is no longer running.' }
        $name = '{0}-{1}-{2}-{3}' -f $trial.Cycle, $trial.Block, $trial.Position, $trial.Condition
        $directory = Join-Path $evidence $name
        [void][IO.Directory]::CreateDirectory($directory)
        Write-Host "Running $name (48 kHz source and capture)"
        $isTarget = $CarryoverDelaySeconds -and $trial.Role -eq 'Target'
        $due = if ($isTarget) { $primer.AfterProbeExitTicks + $CarryoverDelaySeconds * $frequency } else { 0 }
        $idle = if ($CarryoverDelaySeconds -or $IsolationProfile) { $focusedIdleSeconds } else { 1 }
        $discard = $CarryoverDelaySeconds -and $trial.Condition -eq 'CPU'
        $result = Invoke-WprTrial $wpr $probe $endpoint $trial $directory $activeSession -IdleSeconds $idle -DueTicks $due -DiscardTrace:$discard -ProfilePath $profilePath
        $results.Add($result)
        $result | Export-Csv -LiteralPath (Join-Path $evidence 'trials.csv') -Encoding UTF8 -NoTypeInformation -Append
        $status = Invoke-LabChild $probe @('defaults') (Join-Path $directory 'defaults-after')
        $snapshot = Get-Content -LiteralPath (Join-Path $directory 'defaults-after.stdout.txt') -Raw -Encoding OEM
        if ($status -ne 0 -or $defaults -cne $snapshot) { throw "Default playback roles changed or could not be verified after $name." }
        if ($isTarget) {
            [pscustomobject]@{ Arm = $trial.Block; Primer = $trial.Primer; RequestedExitToTargetSeconds = $CarryoverDelaySeconds;
                PrimerExitToTargetLaunchSeconds = ($result.BeforeConditionTicks - $primer.AfterProbeExitTicks) / $frequency;
                PrimerAudioStopToTargetPlaybackSeconds = ($result.PlaybackTicks - $primer.StopRequestTicks) / $frequency;
                PrimerExitToTargetPlaybackSeconds = ($result.PlaybackTicks - $primer.AfterProbeExitTicks) / $frequency;
                PrimerStopToTargetLaunchSeconds = ($result.BeforeConditionTicks - $primer.AfterConditionStopTicks) / $frequency;
                PrimerDiscontinuities = $primer.Discontinuities; PrimerPcmSeconds = $primer.PcmSeconds;
                TargetDiscontinuities = $result.Discontinuities; TargetPositionMismatches = $result.PositionMismatches; TargetQpcRegressions = $result.QpcRegressions;
                TargetPcmSeconds = $result.PcmSeconds; TargetArrivalSeconds = $result.ArrivalSeconds; TargetAcceptancePassed = $result.AcceptancePassed } |
                Export-Csv -LiteralPath (Join-Path $evidence 'carryover-targets.csv') -Encoding UTF8 -NoTypeInformation -Append
        } elseif ($CarryoverDelaySeconds) { $primer = $result }
    }
    Assert-RunnerArchive $probeArchive $probeHash $runner
    $status = Invoke-LabChild $shell ($verifyArgs + @('-EvidenceDirectory', (Join-Path $evidence 'verification-after'))) (Join-Path $evidence 'verification-after')
    if ($status -ne 0) { throw "Final package and endpoint verification failed (exit $status)." }
    if ($XperfPath) {
        foreach ($file in Get-ChildItem -LiteralPath $evidence -Filter 'capture.etl' -File -Recurse) {
            $stats = Join-Path $file.DirectoryName 'etl-stats.txt'
            $status = Invoke-LabChild $XperfPath @('-i', $file.FullName, '-o', $stats, '-a', 'tracestats', '-timespan', 'actual', '-detail') (Join-Path $file.DirectoryName 'xperf')
            if ($status -ne 0 -or ![IO.File]::Exists($stats)) { throw "Xperf could not summarize $($file.FullName) (exit $status)." }
            if ($IsolationProfile) { Assert-IsolationCapture ($file.Directory.Name.Split('-')[-1]) (Get-Content -LiteralPath $stats -Raw) -TraceStats }
        }
    }
} catch {
    $errorText = $_.ToString()
} finally {
    if ($watch) {
        if (![IO.File]::ReadAllText($activeSession)) { [void]$done.Set(); [void]$watch.WaitForExit(5000) }
        $watch.Dispose()
    }
    if ($done) { $done.Dispose() }
    if ($results.Count -and !$CarryoverDelaySeconds) {
        $results | Group-Object Condition | ForEach-Object {
            $group = $_.Group
            [pscustomobject]@{ Condition = $_.Name; Trials = $_.Count; AcceptancePasses = @($group | Where-Object AcceptancePassed).Count;
                Discontinuities = ($group | Measure-Object Discontinuities -Sum).Sum; PositionMismatches = ($group | Measure-Object PositionMismatches -Sum).Sum;
                QpcRegressions = ($group | Measure-Object QpcRegressions -Sum).Sum; PcmPackets = ($group | Measure-Object PcmPackets -Sum).Sum;
                MeanPcmSeconds = ($group | Measure-Object PcmSeconds -Average).Average; MeanArrivalSeconds = ($group | Measure-Object ArrivalSeconds -Average).Average;
                MedianOfTrialArrivalUs = Get-Median $group.MedianArrivalUs;
                MeanLeftSourcePerArrivalSecond = ($group | Measure-Object leftRefillSourcePerArrivalSecond -Average).Average;
                MeanRightSourcePerArrivalSecond = ($group | Measure-Object rightRefillSourcePerArrivalSecond -Average).Average }
        } | Export-Csv -LiteralPath (Join-Path $evidence 'conditions.csv') -Encoding UTF8 -NoTypeInformation
    }
    $acceptanceFailures = @($results | Where-Object { !$_.AcceptancePassed }).Count
    [pscustomobject]@{ Planned = $schedule.Count; Completed = $results.Count; AcceptanceFailures = $acceptanceFailures; Error = $errorText;
        PendingPrivateSession = [IO.File]::ReadAllText($activeSession) } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'result.json') -Encoding UTF8
    [IO.Compression.ZipFile]::CreateFromDirectory($evidence, $archive, [IO.Compression.CompressionLevel]::Optimal, $true)
    $digest = (Get-FileHash -LiteralPath $archive).Hash
    [IO.File]::WriteAllText("$archive.sha256", "$digest  $([IO.Path]::GetFileName($archive))`r`n")
    Write-Host "Evidence: $archive`nSHA-256: $digest"
}
if ($errorText) { throw $errorText }
if ($acceptanceFailures) { throw "$acceptanceFailures trial(s) failed unchanged Probe acceptance. The comparison evidence was saved." }
