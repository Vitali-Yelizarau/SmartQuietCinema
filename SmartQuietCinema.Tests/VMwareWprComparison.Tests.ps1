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
    function Start-LabChild {
        <# .SYNOPSIS
        Runs the substitute Probe with real process lifetime and no audio clients.
        #>
        param([string]$Program, [string[]]$Arguments, [string]$LogPrefix)
        if ($Program -ne 'probe-stub' -or $Arguments[0] -ne 'probe-sequence' -or $Arguments[2] -ne '48000') { throw 'Unexpected Probe invocation.' }
        & $realStart $shell @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $sequenceChild) $LogPrefix
    }
    function Invoke-LabChild {
        <# .SYNOPSIS
        Simulates a successful private WPR recording while checking the production argument order.
        #>
        param([string]$Program, [string[]]$Arguments, [string]$LogPrefix)
        $recorded = [IO.File]::ReadAllText($active)
        if ($Program -ne 'wpr-stub' -or !$recorded -or $Arguments[-2] -ne '-instancename' -or $Arguments[-1] -ne $recorded) { throw 'A WPR operation lacked the recorded private name.' }
        if ($Arguments[0] -eq '-start' -and ($Arguments[0..4] -join ' ') -eq '-start Audio -start CPU -filemode') { return 0 }
        if ($Arguments[0] -eq '-stop') { [IO.File]::WriteAllText($Arguments[1], 'synthetic ETL'); return 0 }
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

    "VMware WPR comparison tests: $passed passed, 0 failed, 0 skipped."
} finally {
    foreach ($file in Get-ChildItem -LiteralPath $temporary -File) { [IO.File]::Delete($file.FullName) }
    [IO.Directory]::Delete($temporary)
}
