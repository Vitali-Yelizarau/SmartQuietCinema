# SmartQuietCinema Development Rules

## Architecture

- `SmartQuietCinema.Core` contains platform-independent DSP logic and interfaces.
- `SmartQuietCinema.Platform.Windows` contains Windows-specific implementations, including WASAPI loopback capture.
- `SmartQuietCinema.Maui` owns the UI and user interaction.
- `SmartQuietCinema.Tests` contains unit tests.
- Do not cross existing project boundaries without prior agreement.

## Primary DSP Goal

- Apply bidirectional adaptive correction: boost quiet, useful sounds and attenuate excessively loud sounds.
- Preserve natural sound, channel balance, and smooth transitions. Protect against sudden loud sounds.
- Do not confuse level-based correction with speech recognition, which is not implemented yet.
- Do not change experimental DSP thresholds without justification and tests.

## Universal and Content-Independent Audio Processing

Develop SmartQuietCinema as a universal, intelligent, adaptive processor for arbitrary system audio. It must work automatically on previously unseen content without content-specific configuration. This principle applies to all future development unless the project owner explicitly changes it.

### Bidirectional Processing

- Amplify quiet, useful audio, including quiet dialogue, whispered speech, and subtle details. Attenuate excessive loudness from sudden impacts, explosions, loud music, and sustained effects.
- Adapt the amount and timing of gain correction to the incoming signal. Preserve natural sound, intelligibility, channel balance, and smooth transitions. Avoid excessive noise amplification, clipping, audible pumping, abrupt gain changes, and unnecessary suppression.
- Evaluate both directions together: stronger attenuation must not compromise quiet-speech intelligibility, and stronger amplification must not introduce unacceptable noise or loudness artifacts.

### No Recording-Specific Tuning

- Do not design DSP around one movie, song, recording, WAV file, timestamp, or isolated event. Do not add hardcoded exceptions or content-specific thresholds to improve a single example.
- A single recording can reveal a general weakness, not establish that a fix generalizes. Treat experimental parameters as hypotheses until validated on diverse audio. Prefer generalizable signal characteristics and justified adaptive behavior to ad hoc corrections.
- Keep universal DSP behavior distinct from any optional user preferences introduced in the future.

### Generalization and Acceptance

- For significant DSP changes, test sudden transients, gradually rising effects, repeated impacts at different intervals, and sustained loud music and effects.
- Also test quiet dialogue and whispers, quiet speech after both silence and loud events, normal speech and moderate-volume content, ambient sounds and low-level noise, and both stereo and genuine multichannel audio.
- Use synthetic reference signals for deterministic regression tests and real-world recordings for controlled listening. A horror-movie scene is one regression example, not the definition of correct behavior.
- Compare candidates against the existing implementation using identical unprocessed inputs, as required by Original Audio Source Integrity. Measure RMS, sample peak, gain behavior, timing, and other relevant metrics; evaluate audible quality through controlled listening and check for regressions on unrelated audio.
- Distinguish confirmed improvements, experimental observations, and unverified assumptions. Lower RMS, lower peaks, passing tests, or an improvement on one recording are not sufficient grounds for claiming better overall quality or declaring a change production-ready.

### Engineering Decision Rule

Before proposing or implementing a DSP change, explicitly answer:

1. What general audio-processing problem does this change address?
2. Why is it a general algorithmic improvement rather than a recording-specific workaround?
3. Which types of audio should benefit?
4. Which types of audio could become worse?
5. How will it be evaluated beyond the recording that exposed the problem?

If these questions cannot be answered with sufficient technical justification, investigate further before modifying the DSP algorithm.

## Original Audio Source Integrity

This policy is mandatory for all future SmartQuietCinema development and testing unless the project owner explicitly changes it.

### Mandatory Input Requirements

- Use the original, unprocessed audio recording as input for DSP experiments, A/B comparisons, regression testing, and candidate evaluation. For real-world movie experiments, use the original capture from the source playback stream, such as `Original.wav`.
- Never feed a DSP-processed WAV, baseline output, or candidate output into another DSP run unless the project owner explicitly authorizes that specific experiment.
- Unit tests may use synthetic signals generated as unprocessed reference inputs.

### A/B Comparison Rules

- Process the same original input independently for the baseline and every candidate. Do not chain or accumulate processing across candidate versions.
- Previously processed files may be used as reference outputs for comparison, not as processing inputs by default.
- Use unambiguous labels: **Original Input** is the unprocessed source; **Baseline Output** is the result from the previous DSP implementation; **Candidate Output** is the result from the experimental implementation. Never refer to the baseline output as the source audio.

### Source Verification and File Safety

- Record the exact input and output file paths for every experiment. For DSP comparisons, verify and record the SHA-256 of the original input and confirm that the baseline and candidate outputs came from identical input audio.
- Never modify or overwrite the original recording, an existing baseline, or an existing candidate output.
- Preserve sample rate, channel count, and other relevant audio format details in experiment reports. Identify any format differences when reporting results.

### Capture Selection and Numbered Outputs

- Use `%LOCALAPPDATA%\SmartQuietCinema\Samples` as the default root for new Windows A/B captures and real-world DSP experiments. Resolve the current user's LocalAppData with the Windows known folder in .NET and `$env:LOCALAPPDATA` in PowerShell. Create the root when needed; report resolution or access failures without falling back to OneDrive. Leave historical captures in place; use the explicit input/output CLI only when requested.
- Enumerate only the immediate subdirectories of the capture root whose names strictly match `yyyyMMdd_HHmmss_fff` and parse as valid timestamps.
- Select exactly the two newest valid capture folders by the timestamp in their names, not by file metadata, duration, content, or previous outputs. Use the newest as the primary reference and the next newest for regression. Report if fewer than two valid folders exist. Verify that each selected folder contains an accessible `Original.wav`; report any failure without substituting an older folder.
- Process only each selected folder's `Original.wav`, independently with fresh DSP state per recording and continuous state across its buffers. Record the capture timestamp, exact input and output paths, source SHA-256, WAV format, sample rate, channel count, duration, and input/output RMS and sample peak. Verify that the source hash is unchanged.
- Publish each offline result in its source capture folder as `ProcessedOutput_N.wav`, starting at `1` and using the lowest available positive integer independently in each folder. Never use an unnumbered `ProcessedOutput.wav`, rename existing recordings, or delete previous outputs.
- Guarantee exclusive, no-overwrite publication rather than relying on a filename check alone. On a naming collision, try the next available number. Publish only a complete, verified WAV; on failure, clean up only staging files created by the current run and leave all pre-existing files untouched.

## Coding Rules

- Use C# and follow existing project conventions.
- Use `namespace SmartQuietCinema.Tests` for new unit tests.
- Reuse existing classes, interfaces, and variable names. Do not introduce abstractions or dependencies without a clear need.
- Account for continuity across audio buffers, multichannel formats, and real-time audio constraints.
- Avoid blocking operations, file I/O, and unnecessary memory allocations in audio callbacks.
- Windows automation scripts must support Windows PowerShell 5.1 unless the project owner explicitly authorizes a newer minimum version.

## Compact C# Formatting

- Prefer concise, clean, readable C#. Prioritize readability, consistency, and compactness over excessive vertical formatting.
- Prefer one line for readable method, local function, constructor, and delegate declarations, including generic signatures, and for method and constructor calls. Keep parameter and argument lists on one line when clear, including lists with four to six items. The number of items alone does not justify placing each on its own line.
- Apply the same preference to assignments, arithmetic, return expressions, simple conditions, short lambdas, properties, and small initializers. Prefer one line for readable constructs up to 160 characters. Evaluate longer constructs individually and use multiple lines where they improve clarity. This preference sets no hard line length limit.
- Keep program behavior unchanged when formatting. Preserve token order, literals, comments, and directives.
- Preserve the project owner's deliberate formatting choices. Do not reformat unrelated code or introduce unnecessary line breaks.
- Apply this policy to future C# and project-owned C++ work in SmartQuietCinema unless the project owner explicitly changes it.

Preferred:

```csharp
_attackCoefficient = CalculateCoefficient(AttackMilliseconds, sampleRate);
LatencyFrames = Math.Max(1, (int)Math.Ceiling(sampleRate * LookaheadMilliseconds / 1000.0));
_delayBuffer = new float[checked(LatencyFrames * channels)];
```

Avoid unnecessary multiline formatting:

```csharp
_attackCoefficient = CalculateCoefficient(
    AttackMilliseconds,
    sampleRate);
```

## Documentation and Comments

### Language

- Write all project documentation exclusively in English. This includes every Markdown documentation file, such as `AGENTS.md`, `README.md`, architecture documents, and development guidelines. All headings, descriptions, instructions, and examples in `AGENTS.md` must be in English.
- Write all source code comments exclusively in English, including inline comments, XML documentation, summaries, parameter descriptions, remarks, and TODO comments. Do not introduce non-English text into technical documentation or source code comments.
- This policy remains in effect unless the project owner explicitly instructs otherwise.
- Give conversational explanations and progress reports to the project owner in Russian unless requested otherwise. This policy does not affect application localization: RESX resources and user-facing translations must use their intended languages.

### XML Documentation

- Place a `///` XML documentation comment immediately above every new class, constructor, method, and unit test, before any attributes. Explain what the code does and why it exists.
- For unit tests, describe the scenario, the expected behavior, and the regression the test is intended to prevent.
- When changing the behavior of an existing documented method, update its XML documentation to match.
- Use `<param>` tags when parameter descriptions add useful information, `<returns>` for meaningful return-value descriptions, and `<remarks>` when technical context, assumptions, limitations, or side effects need explanation. Keep documentation concise and informative.

Example:

```csharp
/// <summary>
/// Calculates the smoothing coefficient used for audio gain transitions.
/// This coefficient ensures consistent behavior across different sample rates.
/// </summary>
/// <param name="milliseconds">The smoothing time constant in milliseconds.</param>
/// <param name="sampleRate">The audio sample rate in Hertz.</param>
/// <returns>The exponential smoothing coefficient.</returns>
private static double CalculateCoefficient(double milliseconds, int sampleRate)
{
    return Math.Exp(-1000.0 / (milliseconds * sampleRate));
}
```

### Comments Inside Methods

- Use `//` comments to explain non-obvious implementation details, important calculations, algorithmic decisions, and special cases. Explain the reasoning behind complex DSP operations when it is not immediately apparent.
- Do not restate what the code already makes clear or comment on every statement.

## Development Workflow

- Before implementation, review the affected files and propose a short change plan.
- Change only files relevant to the task. Add or update regression tests when changing behavior.
- Run appropriate tests when permitted and technically feasible. Distinguish a successful build, passing unit tests, and actual sound-quality validation. Report what was verified.
- Show the final diff and explain the changes. If these rules conflict with the source code, report the conflict. Never make corrections silently.
- Do not create Git commits without explicit permission. Never delete or overwrite the user's existing changes without agreement.
- Do not install packages, perform large-scale refactoring, or change the architecture without permission.

## Current Project Stage

- The latest verified `SmartQuietCinema.Tests` run passed all 133 tests.
- `LookaheadTransientController` implements transient hold, adaptive recovery, and lookahead-assisted quiet gain. The Windows capture path and Offline Audio Test Bench use the original frames to guide quiet gain.
- The processed signal is used for diagnostics, A/B recording, and offline comparisons. Playback of processed system audio is not implemented yet.
- The experimental virtual audio driver builds for Windows x64 and its generated INF passes validation. Driver installation and live PCM delivery remain unverified.
