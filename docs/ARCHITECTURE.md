# Architecture

## Components

- `AnzExplorer.App`: WinUI 3 presentation, navigation/session coordination, sidebar editor, clipboard integration, and first-party file commands.
- `AnzExplorer.Core`: path rules, crash-safe settings persistence, recursive size scanner, and named-pipe client.
- `AnzExplorer.Contracts`: versioned settings, navigation, folder-size, and IPC records shared across processes.
- `AnzExplorer.Indexer`: per-user, below-normal-priority worker with a SQLite WAL cache.
- `AnzExplorer.Core.Tests`: filesystem, cancellation, path, ordering, backup, and recovery tests.

## Folder-size flow

Visible folder rows are marked queued, with one cold recursive request active at once to avoid competing seeks. The UI asks the worker over a current-user-only named pipe. The worker returns a cached aggregate or uses native large-buffer directory enumeration without following reparse-point directories, persists the result, and responds with complete, partial, or unavailable state. If the worker cannot be reached, the UI performs the same cancellable scan in-process so folder navigation remains useful.

Named-pipe JSON messages use a four-byte little-endian length followed by a UTF-8 payload, with a 1 MB maximum. Explicit framing is mandatory on a duplex pipe: stream-oriented JSON deserialization otherwise waits for end-of-stream while the client keeps the pipe open waiting for its response. Consecutive-message and invalid-length regression tests protect this boundary.

Logical size is the sum of accessible descendant file lengths. Partial results are displayed with a `≥` prefix. Cloud files are never opened merely to calculate size, and reparse-point directories are not traversed.

## Persistence

Settings use a versioned JSON document. Save writes to a temporary file with write-through enabled, atomically replaces the primary file, and retains the prior file as a last-known-good backup. A corrupt primary falls back to the backup. Sidebar item IDs and explicit order values make restart and migration ordering deterministic.

The SQLite database is disposable derived data. WAL mode allows reads while updates are committed. Future schema changes must be transactional and rebuild safely when migration is impossible.

## Devices and This PC

Filesystem-backed devices are discovered off the UI thread through `DriveInfo`, including fixed, removable, optical, RAM, and mapped network drives. They appear in a dynamic sidebar section that is intentionally separate from the user's persisted static groups. A lightweight five-second refresh detects mount, removal, readiness, label, and free-space changes without subscribing to a broad management service. The synthetic `anz://this-pc` location renders the same descriptors as navigable drive rows.

## Safety boundaries

The app runs as the current user and never requests elevation at startup. It is standalone and does not replace the Windows shell, desktop, taskbar, or system dialogs. Permanent delete always requires confirmation; ordinary delete uses the Recycle Bin. Shell property UI runs out-of-process in Windows, while third-party context-menu hosting remains a later isolated-helper milestone.
