#Requires -Version 5.1
<# .SYNOPSIS
Exercises the WPR comparison's scheduling, evidence accounting, and private cleanup without starting WPR or any audio stream.
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$scriptPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'SmartQuietCinema.Tools.VirtualAudioProbe\Invoke-VMwareWprComparison.ps1'
$tokens = $null
$parserErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parserErrors)
if ($parserErrors) { throw ($parserErrors | Out-String) }
foreach ($definition in $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    . ([scriptblock]::Create($definition.Extent.Text))
}

$temporary = [IO.Directory]::CreateDirectory((Join-Path ([IO.Path]::GetTempPath()) ('sqc-wpr-test-' + [guid]::NewGuid().ToString('N')))).FullName
$shell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$wprp = Join-Path $temporary 'Isolation.wprp'
$trialDirectories = [Collections.Generic.List[string]]::new()
# WPR splits file paths on ! before loading a profile. Some test sandboxes include ! in their redirected temp root.
if ($wprp.Contains('!')) { $wprp = Join-Path $PSScriptRoot ('sqc-wpr-test-' + [guid]::NewGuid().ToString('N') + '.wprp') }
$passed = 0
try {
    # Every condition occupies every position and precedes every other condition once inside the balanced blocks.
    $schedule = @(Get-TrialSchedule 1)
    $positions = $schedule | Group-Object Condition, Position
    $pairs = for ($index = 0; $index -lt $schedule.Count - 1; $index++) {
        if ($schedule[$index].Block -eq $schedule[$index + 1].Block) { "$($schedule[$index].Condition)-$($schedule[$index + 1].Condition)" }
    }
    if ($schedule.Count -ne 16 -or @($positions | Where-Object Count -ne 1).Count -or @($pairs | Sort-Object -Unique).Count -ne 12) {
        throw 'The Williams schedule is no longer counterbalanced.'
    }
    $passed++

    # Both arms target OFF at the same delay, and both comparison orders appear in the focused design.
    $focused = @(Get-FocusedSchedule 15 '')
    $isolated = @(Get-FocusedSchedule 0 'Sampling')
    if ($focused.Count -ne 8 -or ($focused.Condition -join ',') -ne 'CPU,Off,Off,Off,Off,Off,CPU,Off' -or
        @($focused | Where-Object { $_.DelaySeconds -ne 15 }).Count -or ($isolated.Condition -join ',') -ne 'Sampling,Reference,Reference,Sampling' -or
        @($focused | Where-Object { $_.Role -eq 'Target' -and $_.Condition -eq 'Off' }).Count -ne 4) { throw 'Matched WPR controls were lost.' }
    $passed++

    # The real WPR parser reads all three custom file-mode definitions without opening a recording.
    $wpr = Join-Path $env:SystemRoot 'System32\wpr.exe'
    Write-IsolationProfiles $wprp
    $xml = [xml](Get-Content -LiteralPath $wprp -Raw)
    if ($xml.SelectNodes('//EventProvider|//Stacks|//HardwareCounter').Count -or $xml.SelectNodes('//SystemCollector').Count -ne 1) { throw 'The custom profiles no longer isolate event families.' }
    foreach ($condition in 'Reference', 'Sampling', 'Scheduling') {
        $details = Join-Path $temporary "details-$condition"
        if ((Invoke-LabChild $wpr @('-profiledetails', "$wprp!$condition", '-filemode') $details) -ne 0) {
            throw "WPR rejected $condition. $(Get-Content -LiteralPath "$details.stderr.txt" -Raw)"
        }
        Assert-IsolationCapture $condition (Get-Content -LiteralPath "$details.stdout.txt" -Raw -Encoding OEM)
    }
    $passed++

    # Event rows and loss counters independently reject contaminated, empty, or lossy sampling evidence.
    $sampleStats = "Total # Lost Buffers : 0`r`nTotal # Lost Events : 0`r`n    0x2e  0x00  0x0002      90       1440  Sampled Profile  `r`n"
    Assert-IsolationCapture 'Sampling' $sampleStats -TraceStats
    Assert-IsolationCapture 'Reference' ($sampleStats -replace '(?m)^.*Sampled Profile.*$', '') -TraceStats
    $failures = 0
    foreach ($invalid in ($sampleStats -replace 'Events : 0', 'Events : 1'), ($sampleStats + '    0x24   0x00   0x0005  510  14280  Thread: CSwitch')) {
        try { Assert-IsolationCapture 'Sampling' $invalid -TraceStats } catch { $failures++ }
    }
    if ($failures -ne 2) { throw 'The isolation coverage check accepted contaminated results.' }
    $passed++

    # Probe clock offsets are relative to origin. Startup, teardown, and process exit are distinct from streaming.
    $frequency = [Diagnostics.Stopwatch]::Frequency
    $clock = "Stopwatch: frequency_hz=$frequency, origin_ticks=10000, origin_utc=2026-11-24T22:00:00Z, capture_start_ticks=50, first_play_ticks=80, second_play_ticks=100, stop_request_ticks=700, joined_ticks=720`r`n"
    $samples = @(10099, 10100, 10699, 10700, 10720 | ForEach-Object { [pscustomobject]@{ Phase = 'probe-active'; Ticks = $_; Valid = $true } })
    if ((Set-StreamingPhases $samples $clock) -ne 2 -or ($samples.Phase -join ',') -ne 'probe-startup,streaming,streaming,stream-teardown,after-streaming') {
        throw 'Probe-relative QPC stamps did not recover the exact streaming window.'
    }
    if ((Set-StreamingPhases $samples ($clock -replace "frequency_hz=$frequency", 'frequency_hz=1')) -ne 0) { throw 'A different clock frequency was accepted.' }
    $passed++

    # The deployed query must succeed under PowerShell 5.1, without registering any timer-resolution request.
    Initialize-TimerQuery
    $timer = Read-TimerResolution 'synthetic-check'
    if (!$timer.Valid -or $timer.Status -ne '0x00000000' -or $timer.Ticks -le 0 -or $timer.Phase -ne 'synthetic-check') { throw 'The read-only timer query failed.' }
    $passed++

    # A flag on startup is separate from transport discontinuities. Exact signed arithmetic must preserve the prior wrap-shaped signature.
    $packets = Join-Path $temporary 'packets.csv'
    @'
frames,raw_flags_hex,previous_position_frames,position_frames,position_delta_frames,qpc_delta_100ns,arrival_us,arrival_delta_us
480,0x00000001,,0,,,100.000,
480,0x00000001,0,0,-480,-2,15100.000,15000.000
480,0x00000000,0,480,0,3,16600.000,1500.000
480,0x00000005,480,2061584302080,2061584301120,429496729500000,26600.000,10000.000
0,0x00000000,,,,,27600.000,
'@ | Set-Content -LiteralPath $packets
    @'
next_hresult,get_hresult,release_hresult
0x00000000,0x00000000,0x00000000
0x00000000,,
0x80004005,,
'@ | Set-Content -LiteralPath "$packets.native.csv"
    foreach ($voice in 'left', 'right') { "written_samples`r`n19200`r`n960`r`n960" | Set-Content -LiteralPath "$packets.render-$voice.csv" }
    $metrics = Get-PcmMetrics $packets 48000
    if ($metrics.PcmPackets -ne 4 -or $metrics.EmptyPackets -ne 1 -or $metrics.Discontinuities -ne 2 -or $metrics.PositionMismatches -ne 2 -or
        $metrics.PositionDeltaCounts -notlike '*2061584301120:1*' -or $metrics.QpcRegressions -ne 1 -or $metrics.TimestampErrors -ne 1 -or
        $metrics.NativeNonSuccessRows -ne 1 -or $metrics.MedianArrivalUs -ne 10000 -or $metrics.MedianBeforeRepeatUs -ne 15000 -or
        $metrics.MedianBeforeOrdinaryAdvanceUs -ne 1500 -or $metrics.PcmSeconds -ne 0.04 -or $metrics.ArrivalSeconds -ne 0.0265 -or
        $metrics.leftInitialFillSeconds -ne 0.2 -or $metrics.rightRefillSourceSeconds -ne 0.02) { throw 'Packet or render accounting changed.' }
    $passed++

    # Native failures retain separate output and stderr. Quoting preserves a literal quote and a trailing backslash.
    $childScript = Join-Path $temporary 'native child.ps1'
    "param([string]`$Text)`r`n[Console]::Out.WriteLine(`$Text)`r`n[Console]::Error.WriteLine('native stderr')`r`nexit 7" | Set-Content -LiteralPath $childScript
    $log = Join-Path $temporary 'child'
    $argument = 'C:\directory with a " quote\'
    $exitCode = Invoke-LabChild $shell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $childScript, '-Text', $argument) $log
    if ($exitCode -ne 7 -or (Get-Content -LiteralPath "$log.stdout.txt") -cne $argument -or (Get-Content -LiteralPath "$log.stderr.txt") -ne 'native stderr') {
        throw "Native exit or stream mismatch: exit=[$exitCode], stdout=[$(Get-Content -LiteralPath "$log.stdout.txt")], stderr=[$(Get-Content -LiteralPath "$log.stderr.txt")]."
    }
    $passed++

    # The cleanup watcher can exit normally without a polling loop or a WPR operation.
    [IO.File]::WriteAllText((Join-Path $temporary 'active-session.txt'), '')
    $signal = 'Local\SqcWprTest-' + [guid]::NewGuid().ToString('N')
    $done = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, $signal)
    try {
        $watchArgs = @('-NoProfile', '-Mta', '-ExecutionPolicy', 'Bypass', '-File', $scriptPath, '-OwnerId', "$PID", '-CleanupDirectory', $temporary, '-CompletionSignal', $signal)
        $watch = Start-LabChild $shell $watchArgs (Join-Path $temporary 'watch')
        try {
            $ready = Join-Path $temporary 'watch-ready.txt'
            for ($attempt = 0; $attempt -lt 50 -and ![IO.File]::Exists($ready) -and !$watch.HasExited; $attempt++) { Start-Sleep -Milliseconds 100 }
            if (![IO.File]::Exists($ready) -or $watch.HasExited) { throw 'The cleanup watch did not attach to its owner.' }
            [void]$done.Set()
            if (!$watch.WaitForExit(5000) -or $watch.ExitCode -ne 0) { throw 'The cleanup watch did not exit normally.' }
        } finally { [void]$done.Set(); $watch.Dispose() }
    } finally { $done.Dispose() }
    $passed++

    # A valid digest authenticates the runner bytes. An altered runner or a wrong digest must fail before execution.
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $runner = Join-Path $temporary 'Invoke-VMwareProbeUpdate.ps1'
    $archive = Join-Path $temporary 'probe.zip'
    [IO.File]::WriteAllText($runner, 'synthetic runner')
    $zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Create)
    try { [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $runner, 'Invoke-VMwareProbeUpdate.ps1') } finally { $zip.Dispose() }
    $hash = (Get-FileHash -LiteralPath $archive).Hash
    Assert-RunnerArchive $archive $hash $runner
    [IO.File]::AppendAllText($runner, ' replaced')
    $failures = 0
    foreach ($checksum in $hash, ('0' * 64)) {
        try { Assert-RunnerArchive $archive $checksum $runner } catch { $failures++ }
    }
    if ($failures -ne 2) { throw 'An unverified runner or archive was accepted.' }
    $passed++

    # A failed trace save must attempt only the named cancel and never count as a captured trace.
    $calls = [Collections.Generic.List[string]]::new()
    $cancelCode = 0
    function Invoke-LabChild {
        <# .SYNOPSIS
        Records simulated WPR calls so the test can prove that no unrelated instance is targeted.
        #>
        param([string]$Program, [string[]]$Arguments, [string]$LogPrefix)
        $calls.Add($Arguments -join '|')
        if ($Arguments[0] -eq '-stop') { return 9 }
        return $cancelCode
    }
    $instance = 'SqcWpr-' + [guid]::NewGuid().ToString('N')
    if ((Stop-LabTrace 'wpr-stub' $instance $temporary $true) -or $calls.Count -ne 2 -or $calls[0] -notlike "-stop|*|-instancename|$instance" -or
        $calls[1] -cne "-cancel|-instancename|$instance") { throw 'The trace stop fallback did not remain scoped to the private session.' }
    $cancelCode = 7
    $failures = 0
    foreach ($name in $instance, 'unrelated') {
        try { [void](Stop-LabTrace 'wpr-stub' $name $temporary $false) } catch { $failures++ }
    }
    if ($failures -ne 2 -or $calls.Count -ne 3) { throw 'A failed cancellation or a foreign session was accepted.' }
    $passed++

    # The carryover primer deliberately cancels only its private session and bypasses the slow ETL save.
    $calls.Clear()
    $cancelCode = 0
    if (!(Stop-LabTrace 'wpr-stub' $instance $temporary $true -Discard) -or $calls.Count -ne 1 -or
        $calls[0] -cne "-cancel|-instancename|$instance") { throw 'The primer attempted to merge a trace or cancelled a foreign session.' }
    $passed++

    # Exercise the trial with a real child clock and mocked WPR. The combined case must retain the Probe's failure status and save all timer phases.
    $sequenceChild = Join-Path $temporary 'synthetic sequence.ps1'
    @'
$origin = [Diagnostics.Stopwatch]::GetTimestamp()
Start-Sleep -Milliseconds 900
$stop = [Diagnostics.Stopwatch]::GetTimestamp() - $origin
Write-Output 'Capture: Extensible, 48000 Hz, 2 channels, 32 bits'
Write-Output 'Capture reader: native Windows COM, stimulus=aperiodic, UTC=2026-11-24T22:00:00Z'
Write-Output "Stopwatch: frequency_hz=$([Diagnostics.Stopwatch]::Frequency), origin_ticks=$origin, origin_utc=2026-11-24T22:00:00Z, capture_start_ticks=0, first_play_ticks=1, second_play_ticks=2, stop_request_ticks=$stop, joined_ticks=$stop"
Write-Output 'Transport: FAIL'
exit 1
'@ | Set-Content -LiteralPath $sequenceChild
    $realStart = ${function:Start-LabChild}
    $defaultsChildScript = Join-Path $temporary 'synthetic defaults.ps1'
    @'
param([int]$DelayMilliseconds)
Start-Sleep -Milliseconds $DelayMilliseconds
[Console]::Out.WriteLine('synthetic defaults')
exit 0
'@ | Set-Content -LiteralPath $defaultsChildScript
    $mock = @{ DefaultsDelayMs = 0; CancelDelayMs = 0; CancelExit = 0; LaunchReturnDelayMs = 0; SequenceStarts = 0; SequenceId = 0; DefaultsId = 0; CancelInstance = '' }
    function Start-LabChild {
        <# .SYNOPSIS
        Runs the substitute Probe with real process lifetime and no audio clients.
        #>
        param([string]$Program, [string[]]$Arguments, [string]$LogPrefix, [hashtable]$Launch)
        if ($Program -eq 'probe-stub' -and $Arguments[0] -eq 'defaults') {
            $child = & $realStart $shell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $defaultsChildScript, '-DelayMilliseconds', "$($mock.DefaultsDelayMs)") $LogPrefix -Launch $Launch
            $mock.DefaultsId = $child.Id
            return $child
        }
        if ($Program -ne 'probe-stub' -or $Arguments[0] -ne 'probe-sequence' -or $Arguments[2] -ne '48000') { throw 'Unexpected Probe invocation.' }
        $child = & $realStart $shell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $sequenceChild) $LogPrefix -Launch $Launch
        $mock.SequenceId = $child.Id
        $mock.SequenceStarts++
        if ($mock.LaunchReturnDelayMs) {
            Start-Sleep -Milliseconds $mock.LaunchReturnDelayMs
            $Launch.ReturnTicks = [Diagnostics.Stopwatch]::GetTimestamp()
        }
        return $child
    }
    function Invoke-LabChild {
        <# .SYNOPSIS
        Simulates a successful private WPR recording while checking the production argument order.
        #>
        param([string]$Program, [string[]]$Arguments, [string]$LogPrefix)
        $recorded = [IO.File]::ReadAllText($active)
        if ($Program -ne 'wpr-stub' -or !$recorded -or $Arguments[-2] -ne '-instancename' -or $Arguments[-1] -ne $recorded) { throw 'A WPR operation lacked the recorded private name.' }
        if ($Arguments[0] -eq '-start' -and ($Arguments[0..4] -join ' ') -eq '-start Audio -start CPU -filemode') { return 0 }
        if ($Arguments[0] -eq '-start' -and ($Arguments[0..2] -join ' ') -eq '-start CPU -filemode') { return 0 }
        if ($Arguments[0] -eq '-stop') { [IO.File]::WriteAllText($Arguments[1], 'synthetic ETL'); return 0 }
        if ($Arguments[0] -eq '-cancel') {
            $mock.CancelInstance = $recorded
            Start-Sleep -Milliseconds $mock.CancelDelayMs
            return $mock.CancelExit
        }
        throw 'Unexpected WPR operation.'
    }
    $active = Join-Path $temporary 'active-session.txt'
    $trial = Get-TrialSchedule 1 | Where-Object Condition -eq 'Combined' | Select-Object -First 1
    $synthetic = Invoke-WprTrial 'wpr-stub' 'probe-stub' 'endpoint-id' $trial $temporary $active
    $phases = @(Import-Csv -LiteralPath (Join-Path $temporary 'timer-resolution.csv') | Select-Object -ExpandProperty Phase)
    if ($synthetic.AcceptancePassed -or $synthetic.TransportPassed -or $synthetic.ProbeExit -ne 1 -or $synthetic.StreamingTimerSamples -lt 1 -or
        $phases[0] -ne 'before-condition' -or $phases[1] -ne 'after-condition-start' -or $phases[-2] -ne 'after-probe-exit' -or
        $phases[-1] -ne 'after-condition-stop' -or [IO.File]::ReadAllText($active)) { throw 'The trial lost Probe failure status, timer evidence, or private cleanup.' }
    $passed++

    # A missed deadline is an invalid pair, even if a Probe could still report passing PCM.
    $overdue = [Diagnostics.Stopwatch]::GetTimestamp() - 2 * [Diagnostics.Stopwatch]::Frequency
    $rejected = $false
    try { [void](Invoke-WprTrial 'wpr-stub' 'probe-stub' 'endpoint-id' $trial $temporary $active -DueTicks $overdue) } catch {
        if ($_.ToString() -notmatch 'missed its matched launch deadline') { throw }
        $rejected = $true
    }
    if (!$rejected) { throw 'An overdue target was accepted.' }
    $passed++

    $frequency = [Diagnostics.Stopwatch]::Frequency
    $pairedSchedule = @(Get-FocusedSchedule 2 '')
    $targetTrial = $pairedSchedule[1]
    $controlPrimerTrial = $pairedSchedule[2]
    $defaultsSnapshot = "synthetic defaults`r`n"
    foreach ($dirName in 'cpu-primer', 'off-primer', 'off-target') {
        $dir = [IO.Directory]::CreateDirectory((Join-Path $temporary $dirName)).FullName
        $trialDirectories.Add($dir)
        foreach ($file in Get-ChildItem -LiteralPath $temporary -File -Filter 'packets*') { [IO.File]::Copy($file.FullName, (Join-Path $dir $file.Name)) }
    }
    $cpuDirectory, $offDirectory, $targetDirectory = $trialDirectories.ToArray()

    # A slow successful CPU cancellation overlaps the default snapshot. CPU and OFF use the same primer-exit clock and defer reports.
    $mock.CancelDelayMs = 1050
    $mock.DefaultsDelayMs = 750
    foreach ($primerTrial in $pairedSchedule[0], $controlPrimerTrial) {
        $isCpu = $primerTrial.Condition -eq 'CPU'
        $directory = if ($isCpu) { $cpuDirectory } else { $offDirectory }
        $primer = Invoke-WprTrial 'wpr-stub' 'probe-stub' 'endpoint-id' $primerTrial $directory $active -IdleSeconds 0 -ExpectedDefaults $defaultsSnapshot -DiscardTrace:$isCpu
        $remainingSession = [IO.File]::ReadAllText($active)
        if ($remainingSession -or [IO.File]::Exists((Join-Path $directory 'probe.exit.txt'))) { throw 'Primer cleanup or evidence deferral failed.' }
        $targetDue = $primer.AfterProbeExitTicks + 2 * $frequency
        $target = Invoke-WprTrial 'wpr-stub' 'probe-stub' 'endpoint-id' $targetTrial $targetDirectory $active -DueTicks $targetDue
        $completedPrimer = Complete-WprTrial $primer
        $byOperation = @{}
        foreach ($record in $primer.Timings) { $byOperation[$record.Operation] = $record }
        if ($target.ProbeLaunchTicks -lt $targetDue -or $target.ProbeLaunchReturnTicks -gt $targetDue + $frequency / 4 -or
            !$target.ProbeProcessStartUtc -or $byOperation['defaults-verify'].EndTicks -ge $target.ProbeLaunchTicks -or
            $byOperation['process-evidence'].StartTicks -le $target.ProbeLaunchReturnTicks -or $completedPrimer.Condition -ne $primerTrial.Condition) {
            throw 'Matched launch or delayed evidence accounting failed.'
        }
        if ($isCpu -and ($byOperation['wpr-cancel'].DurationUs -lt 950000 -or $byOperation['defaults-start-process'].EndTicks -gt $byOperation['wpr-cancel'].StartTicks)) {
            throw 'The snapshot failed to start independently of the slow named cancellation.'
        }
        if (!$isCpu -and $byOperation.ContainsKey('wpr-cancel')) { throw 'The Off control unexpectedly invoked WPR.' }
    }
    $passed++

    # A cancellation or defaults query that consumes the deadline must prevent actual target launch under the same 250 ms tolerance.
    foreach ($slow in 'CancelDelayMs', 'DefaultsDelayMs') {
        $mock.CancelDelayMs = 0
        $mock.DefaultsDelayMs = 0
        $mock[$slow] = 2350
        $primer = Invoke-WprTrial 'wpr-stub' 'probe-stub' 'endpoint-id' $pairedSchedule[0] $cpuDirectory $active -IdleSeconds 0 -ExpectedDefaults $defaultsSnapshot -DiscardTrace
        $beforeLaunches = $mock.SequenceStarts
        $rejected = $false
        try { [void](Invoke-WprTrial 'wpr-stub' 'probe-stub' 'endpoint-id' $targetTrial $targetDirectory $active -DueTicks ($primer.AfterProbeExitTicks + 2 * $frequency)) }
        catch { if ($_.ToString() -notmatch 'missed its matched launch deadline') { throw }; $rejected = $true }
        $completedPrimer = Complete-WprTrial $primer
        if (!$rejected -or $mock.SequenceStarts -ne $beforeLaunches -or [IO.File]::ReadAllText($active) -or $completedPrimer.Condition -ne 'CPU') {
            throw "The overdue target was launched or the primer evidence was lost after $slow."
        }
        $passed++
    }

    # A late process-creation return invalidates the trial and reaps the already-created owned Probe.
    $mock.LaunchReturnDelayMs = 400
    $rejected = $false
    $due = [Diagnostics.Stopwatch]::GetTimestamp() + $frequency / 4
    try { [void](Invoke-WprTrial 'wpr-stub' 'probe-stub' 'endpoint-id' $targetTrial $targetDirectory $active -DueTicks $due) }
    catch { if ($_.ToString() -notmatch 'missed its matched launch deadline') { throw }; $rejected = $true }
    $events = Import-Csv -LiteralPath (Join-Path $targetDirectory 'trial-timing.csv')
    if (!$rejected -or !($events | Where-Object { $_.Operation -eq 'probe-start-process' -and $_.ProcessStartUtc }) -or
        (Get-Process -Id $mock.SequenceId -ErrorAction SilentlyContinue)) { throw 'A late launch was accepted or the owned Probe remained alive.' }
    $passed++

    # A failed private cancellation leaves the exact watchdog marker intact and closes the outstanding verification process.
    $mock.LaunchReturnDelayMs = 0
    $mock.CancelDelayMs = 0
    $mock.CancelExit = 5
    $failedCancel = $false
    try { [void](Invoke-WprTrial 'wpr-stub' 'probe-stub' 'endpoint-id' $pairedSchedule[0] $cpuDirectory $active -IdleSeconds 0 -ExpectedDefaults $defaultsSnapshot -DiscardTrace) }
    catch { if ($_.ToString() -notmatch 'Cleanup could not be confirmed') { throw }; $failedCancel = $true }
    if (!$failedCancel -or [IO.File]::ReadAllText($active) -cne $mock.CancelInstance -or (Get-Process -Id $mock.DefaultsId -ErrorAction SilentlyContinue)) {
        throw 'Failed cancellation lost its watchdog marker or left a verifier running.'
    }
    $passed++

    # A mismatched role snapshot must fail the arm after cleanup even when the deadline could still be met.
    [IO.File]::WriteAllText($active, '')
    $mock.CancelExit = 0
    $mock.DefaultsDelayMs = 0
    $beforeLaunches = $mock.SequenceStarts
    $rejected = $false
    try { [void](Invoke-WprTrial 'wpr-stub' 'probe-stub' 'endpoint-id' $pairedSchedule[0] $cpuDirectory $active -IdleSeconds 0 -ExpectedDefaults 'different roles' -DiscardTrace) }
    catch { if ($_.ToString() -notmatch 'Default playback roles changed') { throw }; $rejected = $true }
    if (!$rejected -or $mock.SequenceStarts -ne $beforeLaunches + 1 -or [IO.File]::ReadAllText($active)) { throw 'Mismatched roles did not fail closed.' }
    $passed++

    "VMware WPR comparison tests: $passed passed, 0 failed, 0 skipped."
} finally {
    [IO.File]::Delete($wprp)
    foreach ($directory in $trialDirectories) {
        foreach ($file in Get-ChildItem -LiteralPath $directory -File) { [IO.File]::Delete($file.FullName) }
        [IO.Directory]::Delete($directory)
    }
    foreach ($file in Get-ChildItem -LiteralPath $temporary -File) { [IO.File]::Delete($file.FullName) }
    [IO.Directory]::Delete($temporary)
}
