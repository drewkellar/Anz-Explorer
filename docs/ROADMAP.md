# Roadmap

## Alpha hardening

- Replace recursive managed copy/move with an `IFileOperation` adapter, with per-item progress, cancellation, collision decisions, undo metadata, and elevation handoff.
- Add filesystem watchers, cache invalidation, periodic verification, selected-root crawling, pause-on-battery, cache controls, and indexing progress UI.
- Add sortable column headers, list/icon layouts, thumbnail loading, drag-and-drop, selection/scroll restoration, and type-to-select.
- Add sidebar group creation, group collapse, alias/icon editing, keyboard reorder, drag reorder, and settings import/export UI.
- Add an isolated helper for third-party Shell context-menu extensions and timeouts.

## Beta

- Add Windows Search integration with a cancellable recursive fallback.
- Add an operation queue, conflict UI, error history, sleep/removable/network handling, diagnostics export, and accessibility UI automation.
- Benchmark 100,000-item rendering and million-entry scans; tune bounded queues, cache retention, and memory use against the documented gates.
- Add single-instance activation redirection and optional context-menu registration to the installer.

## Release candidate

- Add signed MSIX packaging, updates, settings/cache migration tests, clean uninstall, Windows VM coverage, performance baselines, and manual data-loss review.
- Add x64 release signing and distribution documentation. ARM64 follows after x64 stabilization.

Full shell replacement, Windows 10, global file-dialog replacement, cloud sync, and default-folder-handler registration remain out of scope for v1.

