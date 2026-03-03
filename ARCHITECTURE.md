# WinPanX Architecture (MVP)

## 1. Purpose
WinPanX is a per-user headless Windows background agent that detects active audio apps, routes each app to one of eight pre-existing virtual render endpoints, applies horizontal stereo pan from window position, mixes all slots into one stereo master, and renders to a real output device.

## 2. Scope and Constraints
- Runtime: .NET 10, C#.
- Audio APIs: WASAPI/MMDevice via NAudio plus P/Invoke where needed.
- Routing: undocumented per-process endpoint policy APIs wrapped behind `IRouter`.
- Host model: user-session background process (not Session 0 service).
- MVP slot model:
  - `Slot 01..07`: dedicated app buses.
  - `Slot 08`: shared overflow bus.
- Tray icon menu only (no full UI); JSON configuration and file logging.

## 3. Endpoint Prerequisite Contract
The environment must provide exactly eight virtual stereo render endpoints discoverable by MMDevice enumeration.

Required startup validation:
1. Match endpoints by configured name prefix.
2. Ensure exactly eight matches.
3. Ensure stereo/shared-mode compatibility.
4. Ensure deterministic ordering into slot indexes 1..8.
5. Fail fast with clear diagnostics if validation fails.

Notes:
- Use stable endpoint IDs for runtime operations.
- Friendly names are for discovery/debug only.
- If `OutputDeviceId` resolves to a virtual slot endpoint, startup fails fast (no automatic fallback output selection).

## 4. Runtime Identity and Tracking Model
Application runtime identity is:
- `ProcessId`
- `ProcessStartTimeUtc`

Reason: PID reuse makes PID-only identity unsafe.

A logical app can own multiple audio sessions but maps to one runtime identity and one assignment.

## 5. App Activity Detection
Inputs:
- Poll reconciliation loop (default 500 ms) over active render endpoints and sessions.
- `WinEventHook` (`EVENT_OBJECT_LOCATIONCHANGE`) for low-latency window movement updates.
- Session state and optional peak meter threshold.

App considered active when:
- Peak meter exceeds threshold (`ActivityPeakThreshold`).

Notes:
- Session state alone is not treated as activity to avoid false positives from silent but long-lived sessions.
- The agent process itself is excluded from tracking.

App considered gone when inactive beyond grace period and/or process exited.

## 6. Assignment Policy
When app becomes active:
1. If already assigned, keep assignment.
2. Else assign lowest free dedicated slot `01..07`.
3. If none free, assign `08` overflow.

Stability rule:
- Never reassign while runtime identity is alive.

Release rule:
- Dedicated slot frees when app is gone after grace period.
- Overflow slot is shared and never owned.

## 7. Window Position to Pan
Desktop span:
- Compute union of all monitor bounds.
- Normalize app window center X to `[0..1]`.
- Convert to pan `[-1..+1]`.

Pan fallback policy when no eligible window:
- Default center (`0.0`) until window appears.

Current MVP pan law (unity center, channel-preserving balance):
- `pan = -1` => mute right channel, left unchanged.
- `pan = 0` => both channels unchanged (matches original level).
- `pan = +1` => mute left channel, right unchanged.

Applied per slot:
- `outL += inL * gainL(pan)`
- `outR += inR * gainR(pan)`

Window source policy:
- Pin each app runtime (`PID + start time`) to the first usable window observed while active.
- Continue tracking pan from that pinned window while valid.
- If the pinned window disappears, fall back to discovering another usable window for that app runtime.

For overflow slot, independent per-app pan is impossible after mixing on one shared endpoint. MVP default: centered overflow pan.

## 8. Routing Model
`IRouter` abstracts undocumented per-process endpoint binding.

Routing behavior:
- Attempt binding for all roles:
  - `eConsole`
  - `eMultimedia`
  - `eCommunications`
- Persist mapping where API supports it.
- Capture per-role success/failure.
- On failure, continue running and log fallback (app stays on system/default routing).

## 9. Audio Pipeline
Per slot input:
- One WASAPI loopback capture per virtual slot endpoint.
- Accepts float/PCM capture formats and converts to internal float processing.
- Requires matching slot/output sample rates in MVP (no resampling stage yet).

Mix engine:
- Apply slot pan (dedicated slots from app pan; overflow policy for slot 8).
- Sum into master stereo buffer.
- Apply soft limiter for headroom and clipping protection.

Output:
- Render master to selected real output endpoint (default or explicit configured ID).
- Reopen render chain when output device changes.

## 10. Concurrency Model
Threads/components:
- Control thread for polling/reconciliation.
- Event callbacks for window/device notifications.
- Audio capture/render callbacks.

Rules:
- Audio callbacks must not block.
- Shared state updates use short lock sections or lock-free snapshots.
- Control-plane operations (routing, assignment, device reopen) run off audio callback threads.

## 11. Configuration
Suggested JSON:

```json
{
  "SlotCount": 8,
  "PollIntervalMs": 500,
  "InactiveGraceSeconds": 3,
  "VirtualEndpointNamePrefix": "WinPanX Slot ",
  "OutputDeviceId": "default",
  "ExcludedProcesses": ["System", "svchost"],
  "ActivityPeakThreshold": 0.001,
  "OverflowPanPolicy": "Center"
}
```

## 12. Logging and Diagnostics
Emit timestamped diagnostic logs for:
- startup endpoint discovery/selection
- slot assigned/freed
- routing result (by role)
- capture/render underrun or overrun
- output device changes and reopen attempts
- startup/runtime failures

Each record should include app runtime identity, process name, slot, endpoint ID, and operation outcome.

## 13. Failure and Recovery Expectations
- Missing/invalid virtual endpoints: startup fail with actionable diagnostics.
- Routing API unavailable/fails: degrade gracefully, continue detection/mixing where possible.
- Device unplug/default switch: auto-rebind output and continue.
- App without window: center fallback pan until window appears.

## 14. Module Boundaries
Primary contracts:
- `IEndpointCatalog`: endpoint discovery, validation, output-device monitoring.
- `IAppTracker`: active app/session/window/pan tracking.
- `IRouter`: per-app endpoint routing abstraction.
- `IMixer`: slot capture, panning, mixdown, and rendering.

These contracts are defined in `src/WinPanX.Core/Contracts`.

## 15. MVP Limitations
- Overflow slot cannot provide independent per-app panning.
- Undocumented routing API behavior can vary by Windows build.
- Low latency is best-effort in shared mode; prioritize stability and glitch resistance.

