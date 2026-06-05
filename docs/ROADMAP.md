# ARServer Roadmap

ARServer is developed as an open-source engineering gateway for IEC 61850 MMS, Modbus TCP, and MQTT workflows.

## Current focus

- Stable Windows desktop release packaging.
- Clear user-facing documentation.
- Reliable Modbus TCP publishing from runtime cache.
- MQTT output for dashboard integration.
- Fast CB acquisition for breaker/status monitoring on selected points.

## Planned improvements

### Acquisition and diagnostics

- Effective polling-time measurement per signal.
- Per-IED runtime statistics.
- Clearer stale/timeout counters.
- Better diagnostic timeline for connect, read, retry, and disconnect events.

### Mapping and validation

- Stronger Modbus address conflict detection.
- Mapping profile export/import.
- Word-order preview for 32-bit values.
- Validation report for selected IED mappings.

### Engineering workflow

- More sample projects and mock data sets.
- Better SCL import filtering.
- Report-friendly mapping summary.
- Optional evidence export for FAT/SAT review.

### UI/UX

- More compact high-density engineering views.
- Improved status badges and acquisition indicators.
- Better multi-IED navigation.
- Cleaner first-run guidance.

## Non-goals

- ARServer is not intended to be a protection relay control system.
- ARServer is not a replacement for engineering validation, cybersecurity hardening, or redundant station-level architecture.
- ARServer does not turn polling into deterministic event capture. Event-grade workflows should use the appropriate event/report mechanism available in the IED design.
