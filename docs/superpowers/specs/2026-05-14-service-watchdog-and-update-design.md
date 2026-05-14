# Service watchdog + manual/auto update — design

**Date:** 2026-05-14
**Branch:** `feature/kiosk-service-robustness`
**Status:** Approved (pending spec review)

## Goal

Two connected changes to `duvc-api`:

1. **Manual update button** — when a user opens `duvc-api.exe` (double-click → tray
   app), a tray menu item shows update status and lets them apply a newer version
   on demand.
2. **Reliable kiosk auto-update** — on a kiosk, updates must apply reliably even
   though the API runs as a non-admin kiosk user who cannot overwrite the binary.
   The installed Windows service becomes responsible for this.

Solving #2 properly also fixes two pre-existing problems: a latent port-3790
conflict, and the lack of crash recovery for the API process.

## Current state

- `duvc-api.exe` is a single-file .NET Framework 4.8 app. Modes: `install`,
  `uninstall`, `service`, `tray`, `run`, `app`, `log`. Double-click → `app`.
- `install` (`ServiceInstaller.Install`) creates the `DuvcApi` service with
  `start= demand`, configures recovery, and writes
  `HKLM\…\CurrentVersion\Run\CellariCameraControl = "<exe>" app`. The `Run` key
  is what actually launches the API on a kiosk, as the logged-on user, because
  Session 0 services cannot access DirectShow/UVC cameras.
- `DuvcApiService.OnStart` starts the HTTP API (`_server.Start()`). On a kiosk,
  `kiosk10.ps1` sets the service to Automatic and starts it — so the Session 0
  service and the user-session `app` process both try to bind port 3790 (latent
  conflict).
- `AutoUpdater` (instantiated by `TrayApp`) polls
  `https://api.github.com/repos/eriksp/duvc-api/releases/latest` every 60 min
  (`DUVC_API_UPDATE_INTERVAL`, first check after 60 s), and auto-applies: download
  `duvc-api.exe` + `duvc-api.exe.sha256`, verify SHA-256, then `ApplyUpdate` writes
  a temp `.cmd` that waits for process exit, `move /Y`s the new exe over the old,
  restarts `"<exe>" app`, and the process calls `Application.Exit()`.
- `AutoUpdater.ApplyUpdate` runs as the kiosk user, who typically cannot overwrite
  the admin-owned `duvc-api.exe` — so silent updates are unreliable on a locked
  kiosk (the `.cmd`'s `move` fails and its `:retry` loop spins).

## Target runtime topology

| Process | Runs as | Responsibility |
|---------|---------|----------------|
| `DuvcApiService` | LocalSystem, Session 0, **auto-start** | Watchdog + updater. Launches and monitors the `app` process in the interactive session; owns the binary swap. Never runs the API. |
| `duvc-api.exe app` | Kiosk user, interactive session | API + camera + tray UI. Sole owner of port 3790. |

The service never binds port 3790 → the conflict is gone. The `HKLM\Run` key is
removed; the service launches `app` via `CreateProcessAsUser`.

When the service is **not** installed (plain double-click, no kiosk provisioning),
`app` runs fully standalone exactly as today, including a self-applied manual
update.

## Components

### C1. `AutoUpdater` (refactored)

Split the current `CheckAndApply()` into independent operations:

- `UpdateInfo CheckForUpdate()` — fetches `releases/latest`, parses tag + asset
  URLs, compares versions. Returns an `UpdateInfo` (version, exeUrl, sha256Url)
  when a newer version exists, otherwise `null`. No side effects. Caches the last
  result in `AvailableUpdate`.
- `AvailableUpdate` — property exposing the last `CheckForUpdate()` result, read
  by the tray UI.
- `ApplyUpdate(UpdateInfo)` — download to temp, verify SHA-256, perform the swap
  (see C2 for the service path; standalone path keeps the existing `.cmd`-based
  self-restart).
- Reused as-is: `FetchLatestRelease`, `IsNewer`, `DownloadFile`, `DownloadString`,
  `ComputeSha256`.

The class no longer owns a polling timer that auto-applies inside `app`. Polling
+ auto-apply moves to the service (C2). Inside `app`, `AutoUpdater` is used in
**check-only** mode to drive the tray menu.

**Depends on:** GitHub releases API, network.

### C2. `DuvcApiService` → watchdog + updater

`OnStart` starts a background worker; `ServiceBase.CanHandleSessionChangeEvent`
is enabled so `OnSessionChange` can react to logon promptly.

**Watchdog loop** (periodic, e.g. every ~10 s, plus on `SessionChange`):
- Determine the active console session (`WTSGetActiveConsoleSessionId`).
- Check whether **any** `duvc-api.exe` process is running in that session
  (by name + session id, not strict PID — so a manually started instance is not
  duplicated).
- If none: `WTSQueryUserToken` → `DuplicateTokenEx` → `CreateEnvironmentBlock` →
  `CreateProcessAsUser` to launch `"<exe>" app` in that session.
- If no user is logged on, `WTSQueryUserToken` fails → wait and retry; logon is
  also caught via `OnSessionChange`.

**Updater trigger** — the update sequence runs when either fires:
- the periodic update-check timer (60 min, `DUVC_API_UPDATE_INTERVAL`); or
- the manual request file is found. The request file is polled on the short
  watchdog cadence (~10 s), not the 60 min timer, so a manual click is acted on
  promptly.

**Update sequence:**
1. `CheckForUpdate()`. If `null`, done.
2. Back up: copy current `duvc-api.exe` → `duvc-api.bak.exe`.
3. Download new exe to temp, verify SHA-256. On mismatch: abort, log, keep running.
4. Stop the `app` process(es) in the active session.
5. Swap: overwrite `duvc-api.exe` (works — running as SYSTEM, ACL-independent),
   and delete the sibling `duvc-cli.exe` so the new exe re-extracts the embedded
   CLI on next launch.
6. Relaunch `app` via `CreateProcessAsUser`.
7. **Rollback:** poll `http://127.0.0.1:<port>/health` for ~30 s. If it never
   returns a healthy response, restore `duvc-api.bak.exe` over `duvc-api.exe`,
   delete `duvc-cli.exe` again, relaunch, and log the rollback.

The service runs as LocalSystem (default for `sc create` with no account), which
holds `SE_TCB_NAME` — required by `WTSQueryUserToken`.

**Depends on:** Win32 `wtsapi32`/`userenv`/`advapi32` P/Invoke, `AutoUpdater` (C1),
the `app` process, the state directory (C4).

### C3. `TrayApp` manual "Update" menu item

A new always-visible menu item, updated by the existing 2 s `UpdateStatus()` timer
which reads `AutoUpdater.AvailableUpdate`:

- Before the first check completes: `"Checking for updates…"` (disabled).
- Up to date: `"Up to date"` (disabled).
- Update available: `"Update to v<X.Y.Z>"` (enabled).

Inside `app`, an `AutoUpdater` instance runs `CheckForUpdate()` on a timer
(check-only — no auto-apply) so the menu reflects status. First check after 60 s,
then every 60 min.

**Click behaviour** — no confirmation dialog, balloon-tip feedback only:
- If the `DuvcApi` service is installed → write the request file (C4); show
  "Update requested…". The service (auto-start; picks up the request on its next
  short-cadence tick) performs the swap and relaunches `app`; the new `app` shows
  "Updated to v<X.Y.Z>".
- If the service is not installed (standalone) → call `ApplyUpdate` directly
  in-process (existing `.cmd`-based self-restart).

**Depends on:** `AutoUpdater` (C1), `ServiceController` to detect service state,
the state directory (C4).

### C4. IPC: update-request file

`install` creates a state directory writable by standard users:
`%ProgramData%\DuvcApi\`, with `icacls … /grant *S-1-5-32-545:(OI)(CI)M`
(`BUILTIN\Users` Modify). The manual button writes `update.request` there (an
empty/timestamped marker). The service polls for the file on its short watchdog
cadence (~10 s); if present, it deletes the file and runs the update sequence.

No SDDL changes, no custom service control codes, no extra IPC technology.

### C5. `install` / `uninstall` changes

`install`:
- Create the `DuvcApi` service with `start= auto` (was `demand`).
- Start the service after creation (so provisioning does not require a reboot).
- Do **not** write `HKLM\Run`. Remove the `CellariCameraControl` Run value if a
  previous install left it.
- Create the `%ProgramData%\DuvcApi\` state directory with the Users-Modify ACL.
- Keep: URL ACL registration, recovery configuration.

`uninstall`:
- Stop and delete the service; remove the URL ACL.
- Remove the legacy `HKLM\Run` value and the legacy scheduled task (already done).
- Leave the state directory (logs/settings live there); only the service is removed.

### C6. `kiosk10.ps1` changes

With `install` now setting `start= auto` and starting the service itself, the
`Set-Service -StartupType Automatic` and `Start-Service` lines in the duvc-api
block become redundant — remove them. `install` handles service lifecycle.

## Data flow

**Kiosk boot:** boot → `DuvcApiService` starts (SYSTEM) → watchdog waits for a
session → kiosk auto-logon → `OnSessionChange` → `CreateProcessAsUser` launches
`app` → API + camera up.

**Auto-update (kiosk):** service updater timer → `CheckForUpdate()` → newer found
→ backup → download + verify → stop `app` → swap exe + drop `duvc-cli.exe` →
relaunch `app` → health check → (rollback if unhealthy).

**Manual update (kiosk):** user clicks "Update to vX" → `app` writes
`update.request` → service updater loop sees it → same sequence as auto-update.

**Manual update (standalone):** user clicks "Update to vX" → `app` calls
`ApplyUpdate` directly → existing `.cmd` self-restart.

## Error handling

- No user logged on: `WTSQueryUserToken` fails → watchdog retries; logon caught by
  `OnSessionChange`.
- Network/API failure during check or download: logged, retried next cycle; no
  state changed.
- SHA-256 mismatch: abort before the swap, keep the running version, log.
- New version unhealthy after swap: automatic rollback to `duvc-api.bak.exe`.
- Manually started `app` instance: watchdog detects by name+session, so it does
  not launch a duplicate; `InstanceGuard` mutex still guards within `app`.
- `CreateProcessAsUser` failure: logged; watchdog retries on the next tick.

## Testing

- **Unit-testable logic** (optional small test project): `IsNewer` version
  comparison, `UpdateInfo`/release-JSON parsing, `CheckForUpdate` result mapping.
- **Manual integration matrix** (real Windows session, elevated, as used earlier
  this session):
  1. Fresh `install` → service auto-starts → `app` launched into the session;
     `/health` returns `v<current>`.
  2. Kill `app` → watchdog relaunches it within a few seconds.
  3. Reboot → kiosk auto-logon → `app` running, no port conflict, no admin
     pre-login.
  4. Publish a newer test release → within the interval the service swaps the exe
     and relaunches; `/health` shows the new version.
  5. Manual button with service installed → `update.request` written → service
     applies → new version.
  6. Manual button standalone (no service) → in-process self-update.
  7. Corrupt/old `.bak` rollback path → unhealthy new exe → service restores
     `.bak` and relaunches.
  8. `uninstall` → service gone, Run key gone, no leftover processes.

## Out of scope

- GitHub API rate-limit hardening (shared-NAT kiosks) — tracked separately.
- Staged rollout / central kill switch.
- Pre-release tag handling in `IsNewer`.
