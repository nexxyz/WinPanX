# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.50.0] - 2026-03-03

### Changed
- Replaced virtual-endpoint routing/mixing architecture with direct per-session channel panning.
- Removed dependency on virtual audio endpoints for runtime operation.
- Added ancestor-process window fallback for Chromium-family multi-process audio sessions.
- Simplified configuration schema to tracking/panning-only keys.
- Updated docs, manual, and release checklist for the new architecture.

### Removed
- Legacy endpoint catalog, policy router, slot assignment, and WASAPI loopback mixer pipeline.

## [0.1.0] - 2026-03-03

### Added
- WinPan X tray agent for per-process audio routing to virtual endpoints.
- Window-position-based stereo pan tracking with WinEventHook integration.
- WASAPI loopback capture and stereo master mix output pipeline.
- Graceful routing reset on shutdown.
- Inno Setup installer with optional run-on-startup task.

### Known Limitations
- Overflow slot cannot provide independent per-app panning.
- Resampling is not yet implemented; slot and output sample rates must match.
- Uses undocumented Windows routing interfaces that may vary by OS build.
