# Security policy

## Supported versions

Security fixes are provided for the latest published 1.x release. Older builds should be upgraded before reporting a problem.

## Reporting a vulnerability

Please use a private GitHub Security Advisory in this repository, or Telegram @vanking_bot, and include:

- the affected version and Windows version;
- reproduction steps and expected impact;
- whether a specially prepared file, database, removable drive or network share is required;
- logs with personal paths removed.

Do not open a public issue for an unpatched vulnerability or attach private user files. Maintainers should acknowledge a report, reproduce it privately, prepare a regression test and publish coordinated release notes.

## Release requirements

A public release must pass locked restore, Release build, all tests, the direct/transitive NuGet vulnerability audit, self-contained publish and installer smoke tests. Version tags must not produce public unsigned binaries. Authenticode signing requires repository secrets named `WINDOWS_SIGNING_CERT_BASE64` and `WINDOWS_SIGNING_CERT_PASSWORD`; without them CI artifacts are for testing only.
