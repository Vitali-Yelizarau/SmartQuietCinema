# Audio Test Bench

From the solution directory, run:

```powershell
dotnet run --project SmartQuietCinema.Tools.AudioTestBench -- "input.wav" "processed.wav"
```

Use a 32-bit IEEE float WAV (standard or extensible) and a destination that does not already exist. The tool preserves the sample rate, channel count, and frame count. It uses the Windows capture DSP order: `AdaptiveDynamicsProcessor` (including its sample peak limiter), then `LookaheadTransientController`, with their default settings. It does not normalize, downmix, or apply additional effects.

## Windows Capture Storage

New Windows A/B captures are stored under `%LOCALAPPDATA%\SmartQuietCinema\Samples\yyyyMMdd_HHmmss_fff`. The app uses the current Windows user's `LocalApplicationData` known folder and creates the capture directory when it saves `Original.wav` and `Processed.wav`. Resolution and write failures are reported through the existing A/B save diagnostics; there is no fallback to Documents or OneDrive. Existing recordings are not migrated. The explicit CLI can still read a historical original when requested.

### Process the Latest Captures

From the solution directory, run the wrapper in Windows PowerShell 5.1 or PowerShell 7 on Windows. The .NET 10 SDK and the Test Bench project's restored packages are still required:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\SmartQuietCinema.Tools.AudioTestBench\Invoke-LatestCaptures.ps1"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\SmartQuietCinema.Tools.AudioTestBench\Invoke-LatestCaptures.ps1" -CaptureRoot "D:\Audio Captures"
```

The default root is `$env:LOCALAPPDATA\SmartQuietCinema\Samples`. The wrapper builds the existing CLI without restoring packages, selects the two newest valid `yyyyMMdd_HHmmss_fff` folders by name, and starts a separate CLI run for each `Original.wav`. It does not substitute an older folder if a selected original is missing or invalid.

Each WAV is staged in a private temporary folder inside its capture directory. The wrapper verifies the output format and frame count and checks the original's SHA-256 before publishing it as the lowest available `ProcessedOutput_N.wav` through a no-overwrite move. The console reports paths, hashes, format, duration, RMS, sample peaks, and status. The script exits with `1` if a selected capture fails, even if the other succeeds; it exits with `0` when both succeed. It does not create a missing capture root or write a numbered file for a failed capture.

Run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\SmartQuietCinema.Tests\Invoke-LatestCaptures.Tests.ps1"` to exercise the wrapper on synthetic recordings in temporary directories without touching the real sample root.

The current MAUI Windows configuration is unpackaged, so the app and PowerShell share this path when run as the same user. If a future packaged configuration virtualizes AppData writes, PowerShell may not see them at the same nominal path. Verify filesystem visibility before supporting that configuration; see [Microsoft's packaged desktop app documentation](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes).

## Alignment and Levels

Like the Windows A/B export, the tool discards the lookahead delay at the start of the processed stream and appends the same number of silent frames at the end. The last input frames are not flushed through the delay line: they have no captured future audio. Inputs no longer than the delay produce a silent file of the same duration. There is no shift to apply when comparing the output against the original; exclude the padded tail when evaluating DSP behavior.

The console reports linear sample peak and RMS across all channels. Input statistics ignore nonfinite samples. Output statistics describe the exported file, including its silent tail. The WAV output contains audio and its format, not a copy of the input's ancillary metadata.

## Named Time Intervals

Repeat `--interval "name:start:end"` after the two WAV paths to compare selected passages:

```powershell
dotnet run --project SmartQuietCinema.Tools.AudioTestBench -- "scene.wav" "scene-processed.wav" --interval "whisper:1.25:2.10" --interval "loud passage:6.0:9.0" --interval "after impact:12.4:13.2"
```

Enter seconds on the **original input timeline**, using a decimal point. Names must be nonempty and unique (case-insensitive). An interval includes frames at or after its start and strictly before its end. Use `0 <= start < end`; the start must be within the input WAV. Intervals may overlap.

The report shows the requested and measured ranges, linear RMS and sample peak on both sides, and `20 × log10(output / input)` for each change in dB. Negative means attenuation. If either level is zero or unavailable, the change is `N/A`; measured silence is still displayed as zero. Both sides are clipped to exclude the artificial output tail. A clipped range is marked `truncated`; a range with no comparable frames shows zero compared frames and `N/A` levels. Reporting does not change the WAV export or the whole-file statistics.
