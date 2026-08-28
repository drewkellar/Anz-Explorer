# Folder-size performance research

## Decision

Anz Explorer uses a native metadata scan with `FindFirstFileExW`, `FindExInfoBasic`, and `FIND_FIRST_EX_LARGE_FETCH` for cold folder sizes. It reads the length and attributes already returned in each directory record, never opens file content, skips directory reparse points, and allows only one recursive cold scan at a time. The cache retains one aggregate row per requested folder rather than one row per file.

This is the best default balance for an unelevated file manager because it works on NTFS, ReFS, FAT/exFAT removable media, and SMB paths. Microsoft documents that `FIND_FIRST_EX_LARGE_FETCH` uses a larger directory-query buffer to improve enumeration performance: <https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-findfirstfileexw>.

## File Pilot comparison

File Pilot's public materials describe a native application and a custom engine built from scratch for responsiveness, but they do not document its folder-size algorithm or state that it reads the MFT. Its July 2026 release notes specifically mention fixing background file-metadata tasks that blocked sorting during rapid view changes, reinforcing the need to isolate metadata work from navigation: <https://filepilot.tech/starlog/2026-07-15-v0.8.1>. Anz Explorer adopts the observable architectural lessons—native metadata enumeration, background work, bounded concurrency, immediate cached results, and UI independence—without assuming an undocumented proprietary implementation.

The direct Anz Explorer probe measured `Documents\.codex` at 948,482,590 logical bytes in 167 ms. This demonstrated that native scanning was not the reported failure. The actual fault was unframed JSON over a duplex named pipe: both processes waited for the other to close the stream. Length-prefixed IPC fixed the deadlock. An end-to-end Documents run then reached zero pending folder-size cells, while the folder-only SQLite database remained 20 KB.

## Options evaluated

| Method | Cold speed | Own cache | Coverage | Decision |
|---|---:|---:|---|---|
| One native metadata traversal | Fast | Folder aggregates only | Local, removable, SMB | Default |
| Windows Search `SYSTEMINDEX` | Very fast when indexed | None beyond Windows' index | Only included/indexed scopes | Later optional accelerator |
| USN/MFT enumeration | Extremely fast namespace enumeration | Additional identity/path data | NTFS/ReFS only; not SMB | Later invalidation accelerator |
| Persistent per-file index | Fast warm results | Large | Broad if continuously maintained | Rejected for default |
| Parallel recursive scans | Sometimes faster on SSD | Small | Broad | Rejected because it causes disk contention and poor interactivity |

Windows Search can query cached properties through its OLE DB provider and use `SCOPE` for deep traversal, but coverage depends on Windows' crawl rules and index state. It therefore cannot be the source of truth for arbitrary folders, USB media, or offline network shares: <https://learn.microsoft.com/windows/win32/search/-search-3x-wds-overview> and <https://learn.microsoft.com/windows/win32/search/-search-sql-folderdepth>.

`FSCTL_ENUM_USN_DATA` enumerates MFT/USN records quickly, but the documented `USN_RECORD` data identifies names, parents, attributes, and changes—not logical file length. The operation is NTFS-specific and unsupported over SMB. It is valuable for detecting which cached folders became stale, not for calculating sizes without additional file queries or a large per-file cache: <https://learn.microsoft.com/windows/win32/api/winioctl/ni-winioctl-fsctl_enum_usn_data>.

## Cache and freshness policy

- SQLite WAL contains only folder path, aggregate bytes, state, timestamp, and error summary.
- Cached results are reused for 30 seconds, then lazily reverified when visible.
- Reparse-point directories are not followed, preventing cycles and unintended cloud or mount traversal.
- Inaccessible descendants produce a partial result shown with a `≥` prefix.
- A future USN cursor can invalidate folder rows on supported local volumes while visible-folder watchers handle non-NTFS and network paths.
- Windows Search may later be used opportunistically only after verifying that the requested scope is included and the result is current enough.
