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

## Coding Rules

- Use C# and follow existing project conventions.
- Use `namespace SmartQuietCinema.Tests` for new unit tests.
- Reuse existing classes, interfaces, and variable names. Do not introduce abstractions or dependencies without a clear need.
- Account for continuity across audio buffers, multichannel formats, and real-time audio constraints.
- Avoid blocking operations, file I/O, and unnecessary memory allocations in audio callbacks.

## Compact C# Formatting

- Prefer concise, clean, readable C#. Prioritize readability, consistency, and compactness over excessive vertical formatting.
- Keep method calls, assignments, expressions, and argument lists on one line when they fit comfortably and remain readable. Do not split short calls or argument lists merely for style.
- Use multiline formatting only when a line becomes excessively long or hard to understand, or when splitting genuinely improves readability. Do not impose arbitrary line length limits on clear code.
- Preserve the project owner's deliberate formatting choices. Do not reformat unrelated code or introduce unnecessary line breaks.
- Apply this policy to all future C# work in SmartQuietCinema unless the project owner explicitly changes it.

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

## English-Only Documentation and Comments

- Write all project documentation exclusively in English. This includes every Markdown documentation file, such as `AGENTS.md`, `README.md`, architecture documents, and development guidelines. All headings, descriptions, instructions, and examples in `AGENTS.md` must be in English.
- Write all source code comments exclusively in English, including inline comments, XML documentation, summaries, parameter descriptions, remarks, and TODO comments. Do not introduce non-English text into technical documentation or source code comments.
- This policy remains in effect unless the project owner explicitly instructs otherwise.
- Give conversational explanations and progress reports to the project owner in Russian unless requested otherwise. This policy does not affect application localization: RESX resources and user-facing translations must use their intended languages.

## Development Workflow

- Before implementation, review the affected files and propose a short change plan.
- Change only files relevant to the task. Add or update regression tests when changing behavior.
- Run appropriate tests when permitted and technically feasible. Distinguish a successful build, passing unit tests, and actual sound-quality validation. Report what was verified.
- Show the final diff and explain the changes. If these rules conflict with the source code, report the conflict. Never make corrections silently.
- Do not create Git commits without explicit permission. Never delete or overwrite the user's existing changes without agreement.
- Do not install packages, perform large-scale refactoring, or change the architecture without permission.

## Current Project Stage

- Latest user-confirmed baseline: 34 passing tests.
- The processed signal is used for diagnostics and A/B recording. Playback of processed system audio is not implemented yet.
- The next planned but unimplemented task is **Transient Hold + Adaptive Recovery** in `LookaheadTransientController`. The goal is to hold attenuation more effectively after a sudden sound while keeping sustained sounds natural.
