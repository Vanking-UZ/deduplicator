# Privacy

Deduplicator is designed to work locally. It does not upload scanned file contents, file lists, hashes, logs or usage telemetry.

## Data stored on this computer

Under `%LOCALAPPDATA%\Deduplicator` the application may store:

- the SQLite catalog containing file names, relative paths, sizes, timestamps and hashes;
- settings, operation history and recovery journals;
- image thumbnails and diagnostic logs;
- files moved to the application quarantine.

Logs can contain full file paths and therefore Windows account names or other personal folder names. Review and redact logs before sharing them. Removing `%LOCALAPPDATA%\Deduplicator` deletes the local catalog and settings, but also permanently removes any files still held in the application quarantine; restore those files first.

## Removable media

When portable indexing is enabled, Deduplicator writes a hidden `.dedupindex` cache to the scanned removable drive. It contains metadata and hashes, not file contents. Disable portable indexing before scanning if the drive must not be modified, and delete `.dedupindex` to remove that cache.

## Network access

The application remains offline unless the user explicitly asks it to check GitHub for an update. That request sends ordinary connection metadata, such as the IP address and user-agent, to GitHub under GitHub's privacy terms. No scan data is included.

## Diagnostic sharing

Diagnostic information is shared only when the user manually copies or sends it. There is no automatic crash-report upload.
