# Tray startup UX: no auto Control Panel, kiosk-aware UI suppression

Date: 2026-07-27
Status: Approved

## Problem

Two distinct problems, both in `TrayApp`:

1. **The Control Panel opens on every launch.** `Program.cs` calls `ShowControlPanel()`
   unconditionally at the end of the `TrayApp` constructor. Because the watchdog service
   relaunches `duvc-api.exe app` into the active console session whenever no instance is
   running there (`EnsureAppRunning`, on a 10s tick and on `SessionLogon` /
   `ConsoleConnect` / `SessionUnlock`), the panel reappears on boot, on unlock, and after
   every watchdog-driven restart. On a kiosk tablet it covers the kiosk browser.

2. **There is no kiosk/non-kiosk distinction at all.** "Kiosk" currently appears only in
   comments. Every launch shows the tray icon, a startup balloon tip, and — if the API
   fails to bind — a modal error dialog with Open/Exit buttons. On a locked-down kiosk
   nobody can dismiss that dialog.

There is a third consequence that becomes important once (1) is fixed: with the panel no
longer auto-opening, **the tray icon becomes the only evidence the API is alive**. Icon
registration already has retry machinery from v1.7.0; what it lacks is coverage for a
repeated early failure, and — once kiosk mode exists — a guard so that machinery does not
un-hide a deliberately suppressed icon. See "Tray reliability" below.

## Architecture context

The installed service does **not** serve the API. It is purely a watchdog and updater; it
launches `duvc-api.exe app` into the active console session, and that session process
serves the API *and* owns the tray icon. API-running and tray-exists are therefore the
same condition, which is why suppressing the tray on kiosks is a UI decision rather than
an availability one.

## Design

### 1. Kiosk signal

A new environment variable, `DUVC_API_KIOSK`.

- Truthy values: `1`, `true`, `yes` — case-insensitive, surrounding whitespace trimmed.
- Unset, empty, or any other value means non-kiosk.
- Read once at startup and cached, alongside the existing `GetPort()` / `GetCameraName()`
  accessors in `Program`.

`kiosk.ps1.txt` sets it at **Machine** scope during the duvc install step. The watchdog
builds the session process's environment with `CreateEnvironmentBlock`, which includes
machine-level variables, so the value reaches the launched `app` process.

Chosen over auto-detection (fragile, silent when wrong, hard to override for testing) and
over a marker file (adds a config channel the app does not otherwise have). An env var
matches all five existing configuration knobs and can be flipped on a dev box without an
install.

### 2. TrayUiPolicy

A single value computed once at startup and passed into `TrayApp`:

```
sealed class TrayUiPolicy
    bool ShowTrayIcon
    bool ShowBalloon
    bool ShowStartupErrorDialog
    static TrayUiPolicy FromEnvironment()
```

- Kiosk: all three false.
- Non-kiosk: all three true.

There is deliberately **no** `ShowPanelOnStart` field. The Control Panel must never
auto-open in either mode, so that is a deletion, not a knob.

The policy exists so the env var is read in exactly one place and the kiosk behaviour is
auditable as a single block rather than four scattered conditionals. This matters because
the codebase has no test harness — correctness has to be verifiable by reading.

### 3. Startup flow changes

All within the `TrayApp` constructor:

- Tray icon visibility comes from `policy.ShowTrayIcon` instead of a hardcoded `true`.
- The startup balloon tip is sent only when `policy.ShowBalloon`.
- The startup-error path shows its modal dialog only when
  `policy.ShowStartupErrorDialog`.
- The unconditional `ShowControlPanel()` call is removed.

The Control Panel remains reachable in non-kiosk mode by tray double-click and via the
tray context menu. Both are unchanged.

**Kiosk API-start failure.** When the API fails to start and no dialog may be shown, the
process logs the error and exits. The watchdog then relaunches it within 10s. This
mirrors what the existing "Exit" button already does and avoids a silent permanent
failure where the process lives on serving nothing. The accepted cost: a *persistent*
failure becomes a ~10s relaunch loop, which is visible in the log.

### 4. Tray reliability

**What already exists (v1.7.0).** The constructor already installs a
`TaskbarCreatedListener` that calls `ReassertNotifyIcon()` on every shell
`TaskbarCreated` broadcast, plus a one-shot 200 ms `bootRetry` timer that re-asserts once
the message pump is up. Explorer restarts and the common boot race are therefore already
covered. An earlier draft of this spec claimed the `TaskbarCreated` subscription happened
too late to matter; that is wrong — the window between constructing the `NotifyIcon` and
subscribing is microseconds, and a listener created later still receives broadcasts from
an Explorer that starts afterwards.

**The correct change is twofold:**

- **Kiosk safety (required, not optional).** `ReassertNotifyIcon()` currently sets
  `Visible = true` unconditionally. Left alone, the 200 ms boot retry and every
  subsequent Explorer restart would resurrect an icon that kiosk mode just suppressed.
  `ReassertNotifyIcon` must return early when `ShowTrayIcon` is false, and the boot-retry
  timer must not be started in kiosk mode. Without this, kiosk suppression silently does
  not work.

- **Extend the one-shot retry into a small bounded schedule (non-kiosk only).** The
  existing retry fires once at 200 ms. On a cold boot the service can launch the app
  before Explorer is ready, so that single attempt can fail with nothing after it except
  a `TaskbarCreated` that may already have passed. Replace the one-shot with re-asserts
  at 200 ms, 5 s, and 20 s, then stop.

`NotifyIcon.Visible` reports no success signal — WinForms swallows a failed
`Shell_NotifyIcon` — so there is no way to query whether registration took. Blind bounded
re-assert is the pragmatic option; the schedule is held to three attempts to limit
visible flicker. This is a heuristic that widens coverage, not a guarantee.

### 5. Out of scope

The RDP / no-console-session gap is **not** addressed here. When nobody is at the physical
console, `WTSGetActiveConsoleSessionId` gives the watchdog no session to launch into, so
there is no API and no tray at all. That is an architectural change to `SessionLauncher`
and gets its own spec so it does not delay or bloat this change.

## Verification

The project compiles `Program.cs` + `AssemblyInfo.cs` with raw `csc` via `build.ps1` and
has no test project, so verification is a manual matrix:

| Scenario | Expected |
|---|---|
| Desktop, `DUVC_API_KIOSK` unset, cold boot | Tray icon present; Control Panel does **not** open; balloon shows |
| Desktop, tray double-click / context menu | Control Panel opens |
| Desktop, port already bound | Modal startup-error dialog appears |
| `DUVC_API_KIOSK=1` | No tray icon, no balloon, no dialog; API answers on the configured port |
| `DUVC_API_KIOSK=1`, port already bound | Error logged; process exits; watchdog relaunches within ~10s |
| Explorer restart (non-kiosk) | Icon returns |

## Files affected

- `src/DuvcApi/Program.cs` — kiosk accessor, `TrayUiPolicy`, `TrayApp` constructor changes,
  `ReassertNotifyIcon` kiosk guard, bounded re-assert schedule.
- `dist/kiosk.ps1.txt` — set `DUVC_API_KIOSK=1` at Machine scope during duvc install.
- `README.md` — document `DUVC_API_KIOSK` alongside the existing environment variables.
