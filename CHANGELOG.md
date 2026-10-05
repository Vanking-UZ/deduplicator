# Changelog

All notable user-visible changes are documented here. The format follows Keep a Changelog and releases use semantic versioning.

## [Unreleased]

### Security

- Replaced the vulnerable SQLitePCLRaw 2.x native SQLite bundle with the 3.x bundle.
- Changed the main desktop process from always-elevated to normal user privileges.
- Added a CI gate for direct and transitive NuGet vulnerabilities.
- Made scan publication and pending index deletions one atomic database transaction.
- Added bounded import/update payloads, database integrity checks, migration backups and crash recovery.

### Changed

- Centralized product versioning in `Directory.Build.props`.
- Added deterministic NuGet lock files and a pinned .NET SDK.
- Added Windows build, test, publish, NSIS installer and install/uninstall smoke automation.
- Rebuilt the navigation, first-run tutorial, compact layout, light/dark themes, scaling and reduced-motion support.
- Added overall percent, elapsed time, ETA, throughput, pause and cancellation to long-running work.
- Added adaptive physical-device scheduling: independent disks run in parallel while HDD partitions share a sequential queue.
- Reworked exact-duplicate and large-catalog queries to use bounded pages instead of whole-database materialization.
- Added cancellable streaming CSV/report generation and bounded operation history/cross-disk pages.
- Added a two-minute per-file watchdog for the external audio fingerprint process.
- Added headless UI regression tests for all product pages.

## [1.0.0] - Unreleased

- Initial release baseline.
