# WinPanX Manual

## What It Does
- Detects apps that are currently producing audio.
- Routes each app to one of the configured virtual endpoints.
- Tracks a selected app window and computes horizontal pan from that window position.
- Mixes all virtual endpoints to one real output device.

## Tray Menu
- `Routing enabled` / `Routing disabled`: toggles audio routing/mixing on or off without exiting the app.
- `Run on startup`: toggles launching WinPanX at user sign-in.
- `Open manual`: opens this file with the OS default/open mechanism.
- `Open config`: opens `winpanx.json`.
- `Open log`: opens `winpanx.log`.
- `Exit`: shuts down gracefully and resets app routing back to system default.

## Config Location
- Default: `%LocalAppData%\WinPanX\winpanx.json`.
- You can pass a custom config path as first argument.

## Log Location
- `winpanx.log` in the same directory as the active config file (default `%LocalAppData%\WinPanX`).

## Notes
- If output resolves to one of the virtual slot endpoints, startup fails fast.
- Overflow slot (`8`) is shared; per-app panning inside overflow is not available in MVP.
- Slot/output sample rates must match in this MVP (no resampling stage).
- Installer builds are available as framework-dependent (smaller) and self-contained (larger).

