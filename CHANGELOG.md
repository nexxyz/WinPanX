# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- Initial public release packaging and documentation set.

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
