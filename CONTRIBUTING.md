# Contributing

## Branch workflow

`main` is the single source of truth. Every feature or fix branch starts from the
latest `main` and goes back into it before the next one begins, so branches never
drift apart.

1. **Start from up-to-date `main`.**
   ```powershell
   git checkout main
   git pull --ff-only
   git checkout -b <short-topic-name>
   ```
   Never branch from another feature branch unless the new work depends on it. If
   it does, merge the first branch into `main` first, then branch from `main`.

2. **Keep the branch current.** If `main` moves while you work, bring it in before
   merging back:
   ```powershell
   git fetch
   git merge origin/main
   ```

3. **Build and test before merging.** `.\scripts\build.ps1` must pass with no
   warnings (warnings are errors) and all tests green.

4. **Merge back promptly, then delete the branch.**
   ```powershell
   git checkout main
   git merge --no-ff <short-topic-name>
   git branch -d <short-topic-name>
   ```
   One finished branch at a time is the goal. Long-lived parallel branches that
   touch `MainWindow.xaml(.cs)` or `Models.cs` will conflict.

## Settings schema changes

`ExplorerSettings.CurrentSchemaVersion` is shared by every branch. Bump it only on
`main` (or in a branch that merges before any other schema change), add a
migration in `AtomicJsonSettingsStore.Migrate`, and add a load test for the
previous version. Older builds accept newer files and keep unknown fields, so
switching builds does not lose settings.
