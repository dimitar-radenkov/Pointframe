# Transcription

Local speech-to-text transcripts and subtitles for recordings with audio.

Part of the [Pointframe knowledge base](../knowledge-base.md). Read the cross-cutting rules there first; this file holds what only this area needs.

## Features

| ID | Feature | Triggered from | Entry point | Telemetry | Tests | Read first |
|---|---|---|---|---|---|---|
| F-24 | Transcripts and subtitles | Runs after a recording with audio; transcription settings | `Pointframe/Services/Transcription/TranscriptionQueue.cs`, `Pointframe/Services/Transcription/TranscriptionService.cs` | `transcript_completed`, `transcript_failed` | `Pointframe.Tests/Services/TranscriptionQueueTests.cs`, `Pointframe.Tests/Services/TranscriptionServiceTests.cs`, `Pointframe.Tests/Services/SubtitleFormatterTests.cs`, `Pointframe.Tests/ViewModels/TranscriptSettingsTests.cs` | [Recording transcription](#recording-transcription), [D-005](#d-005-the-speech-model-is-delivered-by-both-the-installer-and-the-app) |

## Recording transcription

**Responsibility.** After a recording that captured microphone audio, produce `.srt` and `.txt` transcripts next to the MP4, entirely on the local machine. Nothing is uploaded and no API key exists. English only, narration only: the recorder captures a microphone, never system audio, so a meeting or a played video produces nothing.

**Entry points.**

| Trigger | Path |
|---|---|
| A recording finishes | `App.HandleRecordingCompleted` enqueues when `RecordingTranscriptEnabled` and the message's `HadMicrophoneAudio` are both true |
| The user asks for the model | `SettingsViewModel.DownloadTranscriptModelCommand` in the Settings Recording section |
| Setup optional component | The `whispermodel` task in `installer/Pointframe.iss` |

**Flow.**

1. `RecordingHudViewModel.Stop` reads `IScreenRecordingService.IsRecordingMicrophoneEnabled` *before* awaiting `Stop()`, because the flag resets inside it, and passes it on `RecordingCompletedMessage`.
2. `ITranscriptionQueue` (`TranscriptionQueue`) accepts the path and runs jobs serially on one background consumer built on `Channel<string>`.
3. `TranscriptionService` resolves the model through `ITranscriptModelService`. A missing model returns a skip result before ffmpeg is ever started.
4. `IAudioExtractor` (`FfmpegAudioExtractor`) writes a temp 16 kHz mono `pcm_s16le` WAV, the only format Whisper accepts. The temp file is deleted in a `finally`.
5. `ISpeechRecognizer` (`WhisperSpeechRecognizer`) is handed the already-resolved model path and streams `TranscriptSegment` values.
6. `SubtitleFormatter` renders both sidecars; `TranscriptionService` writes them without a byte-order mark.
7. `App.HandleTranscriptionCompleted` marshals to the dispatcher, then reports through `ITrayIconManager.ShowTranscriptBalloon` and the telemetry catalog.

**Key types.** `TranscriptionResult(Success, SrtPath, TxtPath, SkipReason, ErrorMessage, SegmentCount)` distinguishes an expected skip from a genuine failure; `TranscriptionSkipReasons` holds the two skip strings so the service and `App` cannot drift. `TranscriptSegment(Start, End, Text)`.

**Invariants.**

- The model path is resolved once, by `ITranscriptModelService`, and passed to the recognizer. Two independent resolutions previously disagreed: one skipped gracefully, the other threw.
- `TranscriptModelResolver` returns a path only when the file exists, the `AppContext` override included. A stale override otherwise reports a model that is not there and fails inside Whisper instead of skipping.
- Jobs are queued, never cancelled by a newer recording, or a second clip silently discards the first clip's transcript.
- The queue runs on a thread pool thread; every tray call hops back through `Dispatcher.InvokeAsync` because `TaskbarIcon` has dispatcher affinity.
- Every non-success outcome is reported. Reporting only `ErrorMessage` made a missing model look like the feature doing nothing at all.
- `.srt` is UTF-8 without a BOM and CRLF; blank segments are skipped rather than written as empty-bodied cues, or strict parsers drop every cue that follows.

**Tests.** `Pointframe.Tests/Services/TranscriptionServiceTests.cs`, `Pointframe.Tests/Services/SubtitleFormatterTests.cs`, `Pointframe.Tests/Services/TranscriptionQueueTests.cs`, `Pointframe.Tests/Services/FfmpegAudioExtractorTests.cs`, `Pointframe.Tests/Services/TranscriptModelResolverTests.cs`, `Pointframe.Tests/ViewModels/TranscriptSettingsTests.cs`.

**Files.** `Pointframe/Services/Transcription/ITranscriptionService.cs`, `Pointframe/Services/Transcription/TranscriptionService.cs`, `Pointframe/Services/Transcription/ITranscriptionQueue.cs`, `Pointframe/Services/Transcription/TranscriptionQueue.cs`, `Pointframe/Services/Transcription/IAudioExtractor.cs`, `Pointframe/Services/Transcription/FfmpegAudioExtractor.cs`, `Pointframe/Services/Transcription/ISpeechRecognizer.cs`, `Pointframe/Services/Transcription/WhisperSpeechRecognizer.cs`, `Pointframe/Services/Transcription/SubtitleFormatter.cs`, `Pointframe/Services/Transcription/TranscriptModelResolver.cs`, `Pointframe/Services/Transcription/ITranscriptModelService.cs`, `Pointframe/Services/Transcription/TranscriptModelService.cs`, `Pointframe/Services/Transcription/NullTranscriptModelService.cs`, `Pointframe/Models/TranscriptSegment.cs`, `Pointframe/Models/TranscriptionResult.cs`. See [Recording pipeline](recording.md#recording-pipeline) and [D-005](#d-005-the-speech-model-is-delivered-by-both-the-installer-and-the-app).

**Lessons.**

- Lesson: Encoding.UTF8 emits a BOM, which corrupts the first SRT cue

## D-005 The speech model is delivered by both the installer and the app

Decided 2026-09-06.

**Context.** `ggml-base.en.bin` is about 141 MB, far too large to bundle. Delivering it only as an unchecked installer component means anyone who skips the checkbox has no way to get it later: they enable transcripts, record, and nothing happens. Delivering it only in-app leaves setup unable to prepare a machine up front.

**Decision.** Ship both. The installer's optional `whispermodel` task downloads to `{app}\models\`; `SettingsViewModel.DownloadTranscriptModelCommand` downloads to `%LOCALAPPDATA%\Pointframe\models\`. `TranscriptModelResolver` probes, in order: the `AppContext` override, `{app}\models\`, next to the binary, then the per-user folder.

**Consequences.** The per-user copy survives upgrades and reinstalls because it lives outside `{app}`; the installer copy does not, and is removed on uninstall. The installer runs elevated, so it must not write to `{localappdata}` — that would resolve to the administrator's profile, not the installing user's. Settings shows which prerequisite is missing and offers the download, so a skipped component is recoverable. Model URLs live in two places, `installer/Pointframe.iss` and `TranscriptModelService`, and change together.

**Alternatives rejected.** Installer-only, the original plan: unchecked by default, so most installs would never have had the model, with no in-app remedy. In-app only: setup cannot pre-provision a machine, which matters for managed deployments.

**Files.** `Pointframe/Services/Transcription/TranscriptModelResolver.cs`, `Pointframe/Services/Transcription/TranscriptModelService.cs`, `Pointframe/ViewModels/SettingsViewModel.cs`, `installer/Pointframe.iss`. See [Recording transcription](#recording-transcription) and [Runtime paths and external binaries](../knowledge-base.md#runtime-paths-and-external-binaries).
