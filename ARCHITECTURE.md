# WinPanX Architecture

## 1. Purpose
WinPan X is a per-user Windows tray agent that applies per-app stereo panning from window position.

## 2. Runtime model
- Host: WinForms tray app (`WinPanX.Agent`) with no main window.
- Runtime loop: `RuntimeCoordinator` + `CoreAudioAppTracker` + `SessionChannelPanController`.
- Logging: append-only file log (`winpanx.log`).

## 3. App identity
App runtime identity is:
- `ProcessId`
- `ProcessStartTimeUtc`

This avoids PID-reuse collisions.

## 4. Activity detection
`CoreAudioAppTracker` polls active render sessions and marks a session as active when:
- `AudioMeterInformation.MasterPeakValue >= ActivityPeakThreshold`

Tracker inputs:
- session polling (`PollIntervalMs`)
- `WinEventHook(EVENT_OBJECT_LOCATIONCHANGE)` updates for low-latency window movement cache

## 5. Window resolution and pan
Window resolution order for an app runtime:
1. pinned window handle (if still valid)
2. process main window
3. most-recent observed window for that PID
4. ancestor-process window fallback (for multi-process browsers)

Pan mapping:
- desktop virtual screen X normalized to `[-1..+1]`
- left edge `-1`, center `0`, right edge `+1`

## 6. Session pan application
`SessionChannelPanController`:
- Enumerates active render sessions.
- Finds active app pan by PID from tracker snapshot.
- Applies channel gains through COM `IChannelAudioVolume` on each session.
- Stores baseline channel volumes per session and restores them when app becomes inactive, routing is disabled, or app exits.

Balance law (unity center):
- `pan < 0`: keep left at 1.0, attenuate right to `1 + pan`
- `pan = 0`: `L=1.0`, `R=1.0`
- `pan > 0`: attenuate left to `1 - pan`, keep right at 1.0

## 7. Configuration
`winpanx.json` keys:
- `PollIntervalMs` (>= 50)
- `InactiveGraceSeconds` (>= 0)
- `ExcludedProcesses` (case-insensitive names)
- `ActivityPeakThreshold` (`0.0..1.0`)

Unknown keys are ignored.

## 8. Lifecycle behavior
- Startup: loads config, starts tracker/runtime, creates tray icon/menu.
- Disable from tray: stops runtime and restores modified session channel volumes.
- Exit: same restore behavior, then exits tray process.
- Run on startup: toggles HKCU `Run` entry.

## 9. Failure behavior
- If session pan interface is unsupported (`IChannelAudioVolume` missing), that session is skipped and warning is logged once.
- If a window cannot be resolved, pan defaults to center.
- Logging failures never crash the app.

## 10. Current limitations
- Not all apps expose pannable session channel controls.
- Browser process graphs can still yield imperfect window association in edge cases.
- Stereo horizontal panning only; no HRTF/3D rendering.
