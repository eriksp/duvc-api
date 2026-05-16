# Control Panel dialog — design spec

**Date:** 2026-05-16
**Branch:** `feature/kiosk-service-robustness`
**Status:** Approved (brainstorming phase complete; ready for implementation plan)

## Goal

Replace the tray menu's "Open Health Page" item with a richer **Control Panel** window that surfaces app health, service lifecycle, update status, installed paths, and an About box — without leaving the tray app's single-file `csc.exe` build.

## Scope

In scope:

- New `ControlPanelForm` WinForms dialog launched from tray menu or tray double-click.
- Cellari logo (`dist/assets/cellari_logo.svg`) used as app/dialog icon and as branding in the dialog header.
- Health status for API, watchdog, and service.
- Install / Uninstall service buttons (delegating to existing elevation flow).
- Local paths for installed files (exe, state dir, log file) with "Open in Explorer" links.
- "Show Log" button (replaces the removed tray menu item).
- Current and latest version, update status, "Check for updates" button, and "Apply update" button.
- About text with two repo links: `eriksp/duvc-api` and `allanhanan/duvc-ctl`.

Explicitly out of scope (deferred):

- Refactoring the existing health-checking mechanism. The dialog reads from existing state, it does not change how health is gathered.
- Editing installed paths. View + open in Explorer only.
- Internationalisation. UI text remains English, consistent with the rest of the app.
- Per-user persisted window position. The dialog centers every time.
- Pre-release tag handling, staged rollout, rate-limit hardening (already deferred per `project_autoupdater_robustness`).

## Architecture

### New types

- `ControlPanelForm : System.Windows.Forms.Form` — the dialog itself. Single class, contained in `src/DuvcApi/Program.cs` to keep the single-file build contract.
- `ControlPanelData` — small immutable struct holding the current snapshot rendered by the dialog (api health, service status, watchdog status, current/latest version, paths). Built fresh on every refresh.

### Lifecycle

`TrayApp` gets one new field:

```csharp
private ControlPanelForm _controlPanel;
```

`ShowControlPanel()` method:

1. If `_controlPanel == null || _controlPanel.IsDisposed` → construct new form, wire `FormClosed` to null out the field, call `Show()`.
2. Else → `_controlPanel.WindowState = Normal; _controlPanel.BringToFront(); _controlPanel.Activate();`.

The form is **modeless**, single-instance, and owned by the tray app's `ApplicationContext` so it does not block tray operations.

### Refresh model

Inside `ControlPanelForm`:

- A `System.Windows.Forms.Timer` with `Interval = 3000` ms ticks while the form is visible.
- `Timer.Tick` calls `RefreshAll()` which gathers a fresh `ControlPanelData` and updates UI elements.
- Timer starts on `Form.Shown`, stops on `Form.FormClosing`, and is also paused while the form is minimized (`Form.Deactivate` → stop; `Form.Activated` → start + immediate `RefreshAll()`).
- A manual `[Refresh]` button forces an immediate `RefreshAll()` regardless of timer.

All refresh work happens on the UI thread. Network/IO that might block (latest-version check, service-status lookup) runs in `Task.Run` and posts back via `BeginInvoke` — same pattern as existing `AutoUpdater` usage in `TrayApp`.

## Sections (top to bottom)

The dialog is roughly 720 × 580 px, `FormBorderStyle.FixedDialog`, no maximize, no minimize-to-taskbar (the tray icon stays the entry point), centered on screen.

Layout uses a single vertical `TableLayoutPanel` with one row per section; each section is wrapped in a `GroupBox` with a light border. Section spacing: 12 px; outer padding: 16 px.

### 1. Header

- `PictureBox` 64 × 64, `SizeMode = Zoom`, displaying `cellari_logo_256.png` (embedded resource).
- `Label` "DUVC API Control Panel" — Segoe UI 14pt bold.
- `Label` `"Version " + AssemblyInfo.Version + " · " + ModeText` — Segoe UI 9pt, `SystemColors.GrayText`.
  - `ModeText` = `"Running standalone"` when no service is detected, or `"Running with service watchdog"` when the service is installed.

### 2. Health

A 3-row grid:

| Bullet | Label | Detail |
|---|---|---|
| coloured dot | "API" | `OK` / `Degraded` / `Down` + ` (last check: HH:mm:ss)` |
| coloured dot | "Watchdog" | service-mode: `OK` / `Stopped`; standalone: `N/A — standalone mode` (grey) |
| coloured dot | "Service" | `Running` / `Stopped` / `Not installed` (grey) |

Bullet implementation: a `Label` with a custom `Paint` handler that fills a 12 × 12 ellipse in the colour chosen by `RefreshAll()`. Colours:

- Green `#2EBC4F` — OK
- Yellow `#F2A93B` — Warn (e.g. WebSocket-only)
- Red `#D64545` — Bad / Stopped
- Grey `#9E9E9E` — Not applicable / Not installed

Health-status sources:

- **API**: read from the existing tray health-tracking state (currently driving the tray icon colour). Expose it from `TrayApp` via an internal property `LastApiHealth` returning `(status, timestamp)`.
- **Watchdog**: when service is installed and running, watchdog status = `OK`. When service is installed but stopped, `Stopped` (red). When no service is installed, `N/A`.
- **Service**: `ServiceController("DuvcApiService").Status` wrapped in try/catch. `InvalidOperationException` from missing service → `Not installed`. `Win32Exception` for access-denied → `Access denied`.

### 3. Service

- `Label` `"Status: " + serviceStatusText`.
- Two buttons side by side:
  - `[ Install Service ]` — enabled only when service is not installed. Calls into the existing elevation path used by the tray's install flow (`RunSelfElevated("install")`).
  - `[ Uninstall Service ]` — enabled only when service is installed. Calls `RunSelfElevated("uninstall")`.

Both buttons trigger a balloon-tip ("Restarting elevated to install/uninstall service…") and close the Control Panel since the elevated child will replace the running app instance.

### 4. Update

- Three labels in a single row:
  - `Current: <AssemblyInfo.Version>`
  - `Latest: <AutoUpdater.AvailableUpdate?.Version ?? "—">`
  - `Status: <Checking / Up-to-date / Update available / Error>`
- Two buttons:
  - `[ Check for updates ]` — calls `AutoUpdater.CheckForUpdate(force: true)` async, then refreshes the dialog. Disabled while a check is in flight.
  - `[ Apply update ]` — enabled only when `AutoUpdater.AvailableUpdate != null` and newer than current. Calls the same `OnUpdateClicked()` path the tray menu uses, so the service-IPC vs in-process branching logic is not duplicated.

### 5. Installed files

Three rows. Each row: label (left), monospace path (middle, `Font = "Consolas", 9pt`), action links (right).

| Item | Path source | Actions |
|---|---|---|
| Executable | `Paths.ExePath` | `[Open folder]` |
| State directory | `Paths.StateDir` | `[Open folder]` |
| Log file | `Paths.LogFile` | `[Open folder]` `[Show Log]` |

`[Open folder]` runs `Process.Start("explorer.exe", "/select,\"" + path + "\"")` so Explorer opens the parent folder with the file pre-selected. `[Show Log]` reuses `TrayApp.ShowLog()`.

If a path does not exist yet (e.g. log file hasn't been written), the path is still shown but the open links are disabled.

### 6. About

Three lines:

- `"DUVC API — Cellari kiosk camera control"` (regular text).
- `LinkLabel` `"github.com/eriksp/duvc-api"` → opens `https://github.com/eriksp/duvc-api` via `Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })`.
- `LinkLabel` `"duvc-cli upstream: github.com/allanhanan/duvc-ctl"` → opens `https://github.com/allanhanan/duvc-ctl` the same way.

### Footer

Right-aligned, separated from the About section by a thin horizontal line:

- `[Refresh]` — forces immediate refresh.
- `[Close]` — closes the form (does not exit the app).

## Tray menu changes

Existing menu items removed:

- `"Open Health Page"`
- `"Show Log"`

New menu item inserted at the top:

- `"Show Control Panel"` → calls `ShowControlPanel()`.

Tray double-click handler changes from `ShowLog()` to `ShowControlPanel()`.

The existing Update item, Install/Uninstall items, Exit item are left untouched — Control Panel is additive, not a replacement for elevation-driven actions.

## SVG → raster conversion

The build pipeline cannot consume SVG, so we pre-render once and check in the rasters.

### New script: `scripts/convert-icon.ps1`

- Input: `dist/assets/cellari_logo.svg`.
- Outputs:
  - `dist/assets/cellari_logo_256.png` — 256 × 256, transparent background.
  - `dist/assets/cellari_logo.ico` — multi-size (16, 32, 48, 256), transparent background.
- Uses Inkscape if found (`Get-Command inkscape` succeeds), otherwise prints a clear error with install instructions. Does NOT silently fall back to a low-quality renderer.
- Idempotent: re-run produces byte-identical output if SVG hasn't changed.
- Run only when SVG changes. The PNG + ICO are committed to git.

### `build.ps1` changes

Add two `/resource:` arguments to `csc.exe`:

```
/resource:$dist\assets\cellari_logo_256.png,cellari_logo_256.png
/resource:$dist\assets\cellari_logo.ico,cellari_logo.ico
```

Validate the two raster files exist before the compile invocation; fail with `throw "Run scripts/convert-icon.ps1 to regenerate raster icons"` if missing.

### Runtime loading

Add a small helper inside `Program.cs`:

```csharp
internal static class EmbeddedAssets
{
    public static Image LoadPng(string resourceName) { /* GetManifestResourceStream → Image.FromStream */ }
    public static Icon LoadIcon(string resourceName) { /* GetManifestResourceStream → new Icon(stream) */ }
}
```

`ControlPanelForm.Icon` is set to `EmbeddedAssets.LoadIcon("cellari_logo.ico")`. The header `PictureBox.Image` is set to `EmbeddedAssets.LoadPng("cellari_logo_256.png")`.

The tray `NotifyIcon` keeps its existing programmatic status icons (red/green/yellow dots) — switching the tray icon to the logo is out of scope.

## Threading and error handling

- All UI mutation goes through the form's UI thread via `Invoke` / `BeginInvoke`.
- Latest-version check: `Task.Run(() => AutoUpdater.CheckForUpdate(force: true))` with a `ContinueWith` posting back. If it throws, status becomes `Error: <ex.Message>` (truncated to 80 chars) and the button re-enables.
- `ServiceController` access is synchronous and fast, but wrapped in try/catch — any exception maps to `Service: Unknown`.
- Health-state read is a property access, so no locking concerns beyond ensuring the writer (existing health-check loop) writes the `(status, timestamp)` tuple atomically. Use `volatile` on the backing field or a `lock`.

## Testing

No automated test harness exists for this project (per existing plan). Verification contract:

1. `build.ps1` exits 0 with the new resources embedded.
2. Manual smoke checks added to the implementation plan, including:
   - "Show Control Panel" from tray menu opens the dialog.
   - Double-click on tray icon opens the dialog (not the log window).
   - Show Log is no longer in the tray menu.
   - Dialog refreshes within ~3 s of changing service state in `services.msc`.
   - `[Open folder]` opens Explorer at the right location.
   - `[Check for updates]` triggers the same `AvailableUpdate` flow that the tray uses.
   - About links open the correct URLs in the default browser.
   - Closing the dialog and re-opening returns a fresh instance.
   - Open dialog, then click "Install Service" → app restarts elevated and installs; Control Panel re-opens after restart in service-mode shows watchdog OK.

These checks become items 9–17 in the existing manual integration matrix (Task 7 of the prior plan).

## Open risks

- The current tray app exposes health state implicitly via icon colour changes; we need a clean property to read it. If the existing structure tangles health with rendering, the implementation plan will include a targeted small refactor to extract a `HealthState` field. This is scoped narrowly.
- Inkscape may not be on every dev machine. Since PNG/ICO are checked in, this only matters when the SVG changes. The plan documents this in the README.
- `csc.exe` `/resource:` paths are case-sensitive when read at runtime via `GetManifestResourceStream`. The implementation must use the exact short names (`cellari_logo_256.png`, `cellari_logo.ico`) consistently.

## File-level changes summary

| File | Change |
|---|---|
| `src/DuvcApi/Program.cs` | Add `ControlPanelForm`, `EmbeddedAssets`, expose health state on `TrayApp`, modify tray menu and double-click handler. |
| `build.ps1` | Add raster validation and two `/resource:` arguments. |
| `scripts/convert-icon.ps1` | New. SVG → PNG + ICO via Inkscape. |
| `dist/assets/cellari_logo_256.png` | New (generated, committed). |
| `dist/assets/cellari_logo.ico` | New (generated, committed). |
| `dist/assets/cellari_logo.svg` | Already present. Unchanged. |
| `docs/superpowers/specs/2026-05-16-control-panel-dialog-design.md` | This file. |
