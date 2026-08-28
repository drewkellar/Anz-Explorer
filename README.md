# Anz Explorer

Anz Explorer is a native Windows 11 file manager focused on predictable navigation and immediately visible folder sizes. It resembles Windows File Explorer while keeping its left navigation panel entirely user-controlled.

## Current alpha

The repository contains a buildable, self-contained WinUI 3 application with:

- Tabs, back/forward/up navigation, an address bar, current-folder search, and session restoration.
- A static sidebar that never injects recent locations. Locations can be added, removed, and reordered, and every change is written atomically with a last-known-good backup.
- Details view with asynchronous sizes for folders and files.
- A separate low-priority folder-size worker, local SQLite WAL cache, named-pipe protocol, cancellation, partial-result reporting, and an in-process recovery path.
- Length-prefixed duplex IPC with bounded messages and regression coverage, preventing metadata requests from waiting indefinitely for stream closure.
- Native large-buffer directory enumeration, serialized cold scans, and a folder-only cache designed to minimize seeks, CPU, and cache growth.
- A dynamic “This PC” location and sidebar drive section that refreshes local, removable, optical, and mapped network drives as they appear or disappear.
- Open, create folder, rename, copy, cut, paste, Recycle Bin delete, permanent delete, properties, and multi-selection.
- Familiar keyboard shortcuts and native light, dark, scaling, high-contrast, and accessibility behavior supplied by WinUI.
- No account, cloud service, telemetry, advertising, or elevation requirement.

This is an alpha, not yet a safe wholesale replacement for File Explorer. See [ROADMAP.md](docs/ROADMAP.md) for remaining parity and release work.

The folder-size design and alternatives are documented in [FOLDER_SIZE_RESEARCH.md](docs/FOLDER_SIZE_RESEARCH.md).

## Requirements

- Windows 11 24H2 or newer, x64.
- .NET 10 SDK 10.0.302 or newer for development.
- The application output itself is self-contained and does not require a separately installed .NET or Windows App SDK runtime.

## Build and test

```powershell
.\scripts\build.ps1
```

Run the debug build:

```powershell
.\src\AnzExplorer.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\AnzExplorer.exe
```

An optional per-user “Open in Anz Explorer” context-menu entry can be installed and removed with the scripts under `scripts/`. These scripts do not replace `explorer.exe` or change the default folder handler.

## Local data

Settings, backups, logs, and the folder-size database live under `%LOCALAPPDATA%\Anz Explorer`. Paths are not written to logs during normal operation. Removing that directory resets local state.
