# Tray Startup UX Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the Control Panel from auto-opening on every launch, and add a `DUVC_API_KIOSK` mode that suppresses all desktop chrome while keeping the tray icon reliable on normal desktops.

**Architecture:** A single `TrayUiPolicy` value is computed once from the environment and passed into `TrayApp`. It gates three things — tray icon, balloon tip, startup error dialog. The Control Panel auto-open is deleted outright (it must never auto-open in either mode). Existing icon-reregistration machinery gains a kiosk guard and a slightly wider retry schedule.

**Tech Stack:** C# on .NET Framework 4.8, WinForms. Compiled by `build.ps1` with raw `csc.exe` from `v4.0.30319`. Single source file: `src/DuvcApi/Program.cs`.

## Global Constraints

- **C# 5 only.** `csc.exe` from `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319` is the C# 5 compiler. Do **not** use string interpolation (`$"..."`), `nameof`, expression-bodied members (`=>` on properties/methods), auto-property initializers, null-conditional (`?.`), or `out var`. Auto-properties with `private set`, lambdas, ternaries, and object initializers are fine.
- **No test framework exists.** There is no `tests/` directory and no test project. `build.ps1` compiles `Program.cs` + `AssemblyInfo.cs` only. Verification is: compile succeeds, then run the app and observe. Every task below has explicit run-and-observe steps.
- **Never build to `dist\duvc-api.exe` while an instance is running** — `csc` fails with CS0016 on the file lock. Always use `-OutputPath` with a scratch path.
- **Build from a short path.** Use `C:\dvb\out` as the scratch output directory.
- `DUVC_API_KIOSK` truthy values are exactly `1`, `true`, `yes` — case-insensitive, whitespace-trimmed. Anything else, including unset and empty, is non-kiosk.
- Launch the app in the **foreground** when verifying. Long-lived processes started from a backgrounded call can be killed when that call is torn down.

## Setup (do once before Task 1)

```bash
mkdir -p /c/dvb/out
```

**Where the log lives.** `Paths.StateDir` is the *directory containing the running exe*, not
`%ProgramData%` (that is the pre-2026-05 legacy path, retained only for cleanup). For a build
at `C:\dvb\out\duvc-api.exe` the log is therefore **`C:\dvb\out\duvc-api.log`**.

Create the port-conflict helper used by Task 2. A `TcpListener` is used rather than an
`HttpListener` because it binds without administrator rights:

```bash
cat > /c/dvb/out/hold-port.ps1 <<'EOF'
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 3790)
$listener.Start()
Write-Host "Holding TCP 3790."
Start-Sleep -Seconds 600
$listener.Stop()
EOF
```

**Clear the field for verification — this is mandatory, not hygiene.** `TrayApp` uses a
process-wide instance guard: if any other `duvc-api.exe` already holds it, a scratch build
launched with `app` **exits silently and does nothing**, and every verification step below
would produce a false pass. The dev machine typically has both the `DuvcApi` service and a
`dist\duvc-api.exe` instance running.

Stop them before verifying (the service stop needs an elevated shell):

```bash
powershell -NoProfile -Command "Stop-Service DuvcApi -Force -ErrorAction SilentlyContinue; Get-Process duvc-api -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 2; Get-Process duvc-api -ErrorAction SilentlyContinue"
```

Expected: no output from the final `Get-Process` — nothing named `duvc-api` is running.

Note that the watchdog service relaunches `app` within ~10 s whenever it is running, so it
must stay stopped for the duration of verification. **Restart it when all tasks are done:**

```bash
powershell -NoProfile -Command "Start-Service DuvcApi"
```

Verify a clean baseline build before changing anything:

```bash
powershell -NoProfile -File build.ps1 -OutputPath "C:\dvb\out\duvc-api.exe"
```

Expected output: `Built C:\dvb\out\duvc-api.exe`

---

### Task 1: Kiosk detection, TrayUiPolicy, and UI suppression

**Files:**
- Modify: `src/DuvcApi/Program.cs` (add kiosk accessor near `GetPort`, add `TrayUiPolicy` class before `TrayApp`, modify `TrayApp` constructor and `ReassertNotifyIcon`)
- Modify: `README.md:338`

**Interfaces:**
- Produces: `Program.IsKioskMode` (`static bool` property). `TrayUiPolicy` with `bool ShowTrayIcon`, `bool ShowBalloon`, `bool ShowStartupErrorDialog`, and `static TrayUiPolicy FromEnvironment()`. `TrayApp` gains a `private readonly TrayUiPolicy _ui` field. Tasks 2 and 3 both consume `_ui`.

- [ ] **Step 1: Add the kiosk accessor to `Program`**

Insert immediately after the closing brace of `GetPort()` in `src/DuvcApi/Program.cs` (currently ends at line 114):

```csharp
        // Kiosk installs run locked down: no tray icon, no balloon, and no modal
        // dialog a kiosk user could never dismiss. Set machine-wide by
        // kiosk.ps1.txt at install time; it reaches the watchdog-launched session
        // process through CreateEnvironmentBlock, which includes machine vars.
        private static readonly bool KioskMode = ReadKioskMode();

        public static bool IsKioskMode
        {
            get { return KioskMode; }
        }

        private static bool ReadKioskMode()
        {
            var raw = Environment.GetEnvironmentVariable("DUVC_API_KIOSK");
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }
            var value = raw.Trim();
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }
```

- [ ] **Step 2: Add the `TrayUiPolicy` class**

Insert immediately before the line `internal sealed class TrayApp : ApplicationContext` (currently line 1929):

```csharp
    // Which pieces of desktop UI the tray process may show. Computed once so the
    // kiosk decision lives in exactly one place instead of scattered env lookups.
    internal sealed class TrayUiPolicy
    {
        public bool ShowTrayIcon { get; private set; }
        public bool ShowBalloon { get; private set; }
        public bool ShowStartupErrorDialog { get; private set; }

        private TrayUiPolicy(bool showTrayIcon, bool showBalloon, bool showStartupErrorDialog)
        {
            ShowTrayIcon = showTrayIcon;
            ShowBalloon = showBalloon;
            ShowStartupErrorDialog = showStartupErrorDialog;
        }

        public static TrayUiPolicy FromEnvironment()
        {
            if (Program.IsKioskMode)
            {
                return new TrayUiPolicy(false, false, false);
            }
            return new TrayUiPolicy(true, true, true);
        }
    }
```

- [ ] **Step 3: Add the `_ui` field and initialise it first**

Add this field to `TrayApp` immediately after `private string _apiStartError;` (currently line 1953):

```csharp
        private readonly TrayUiPolicy _ui;
```

Then make it the very first statement of the `TrayApp(bool startServer)` constructor, before the `InstanceGuard.Acquire()` check, so it is never null on any path:

```csharp
        private TrayApp(bool startServer)
        {
            _ui = TrayUiPolicy.FromEnvironment();

            if (!InstanceGuard.Acquire())
            {
                return;
            }
```

- [ ] **Step 4: Gate the tray icon's initial visibility**

Change the `NotifyIcon` initializer (currently lines 1985-1990) from `Visible = true` to:

```csharp
            _notifyIcon = new NotifyIcon
            {
                Icon = _badIcon,
                Visible = _ui.ShowTrayIcon,
                Text = Program.AppTitle
            };
```

- [ ] **Step 5: Guard `ReassertNotifyIcon` against un-hiding a kiosk icon**

This is required, not optional — without it the boot-retry timer and every Explorer restart would put the suppressed icon straight back. Add the early return at the top of `ReassertNotifyIcon()` (currently line 2292):

```csharp
        private void ReassertNotifyIcon()
        {
            // Kiosk mode deliberately hides the icon. The boot retry and every
            // Explorer TaskbarCreated broadcast would otherwise resurrect it.
            if (!_ui.ShowTrayIcon)
            {
                return;
            }

            try
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Visible = true;
                Logger.Info("Taskbar (re)created; re-registered tray icon.");
            }
            catch (Exception ex)
            {
                Logger.Error("Reassert tray icon failed: " + ex.Message);
            }
        }
```

- [ ] **Step 6: Gate the startup balloon tip**

Wrap the balloon block (currently lines 2053-2056):

```csharp
            if (_ui.ShowBalloon)
            {
                _notifyIcon.BalloonTipTitle = Program.AppTitle;
                _notifyIcon.BalloonTipText = string.Format(CultureInfo.InvariantCulture,
                    "Camera API running on port {0}", Program.GetPort());
                _notifyIcon.ShowBalloonTip(3000);
            }
```

- [ ] **Step 7: Document the variable in README**

In `README.md`, add a line after `- `DUVC_CLI_PATH` (override embedded `duvc-cli.exe` path)` (line 338):

```markdown
- `DUVC_API_KIOSK` (`1`/`true`/`yes` enables kiosk mode: no tray icon, no balloon, no dialogs)
```

- [ ] **Step 8: Build**

```bash
powershell -NoProfile -File build.ps1 -OutputPath "C:\dvb\out\duvc-api.exe"
```

Expected: `Built C:\dvb\out\duvc-api.exe` with no CS errors. If you see `CS1056`/`CS1525` around new code, you used a C# 6+ feature — see Global Constraints.

- [ ] **Step 9: Verify non-kiosk still shows icon and balloon**

Run in the foreground:

```bash
powershell -NoProfile -Command "$env:DUVC_API_KIOSK=$null; & C:\dvb\out\duvc-api.exe app"
```

Expected: tray icon appears; balloon "Camera API running on port 3790" appears. (The Control Panel still auto-opens — that is Task 2.) Exit via the tray menu's Exit item.

- [ ] **Step 10: Verify kiosk mode suppresses icon and balloon**

```bash
powershell -NoProfile -Command "$env:DUVC_API_KIOSK='1'; & C:\dvb\out\duvc-api.exe app"
```

Expected: **no** tray icon, **no** balloon. The API still answers — confirm from a second shell:

```bash
curl -s http://127.0.0.1:3790/health
```

Expected: a JSON health payload. Then stop it:

```bash
powershell -NoProfile -Command "Get-Process duvc-api -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq 'C:\dvb\out\duvc-api.exe' } | Stop-Process -Force"
```

- [ ] **Step 11: Commit**

```bash
git add src/DuvcApi/Program.cs README.md
git commit -m "Add DUVC_API_KIOSK mode and TrayUiPolicy; suppress tray icon and balloon on kiosks"
```

---

### Task 2: Stop auto-opening the Control Panel; gate the startup error dialog

**Files:**
- Modify: `src/DuvcApi/Program.cs:2064-2077` (end of the `TrayApp` constructor)

**Interfaces:**
- Consumes: `_ui.ShowStartupErrorDialog` from Task 1.
- Produces: no new symbols.

- [ ] **Step 1: Replace the startup-error block and delete the auto-open**

Replace the whole block currently at lines 2064-2077 (the comment, the `if (!string.IsNullOrEmpty(_apiStartError))` block, and the trailing `ShowControlPanel();`) with:

```csharp
            // Surface startup problems immediately. If the API failed to start
            // (typically a port conflict with the installed service or a stale
            // tray instance), let the user open the Control Panel to clean up
            // or exit. The Control Panel is never opened unprompted: the
            // watchdog relaunches "app" on boot, unlock, and every 10 s tick,
            // so auto-opening made the window reappear constantly.
            if (!string.IsNullOrEmpty(_apiStartError))
            {
                if (!_ui.ShowStartupErrorDialog)
                {
                    // Nobody can dismiss a dialog on a kiosk. Log and exit so the
                    // watchdog relaunches us within ~10 s and retries the bind.
                    Logger.Error("API failed to start in kiosk mode; exiting for watchdog retry: " + _apiStartError);
                    ExitThread();
                    return;
                }

                if (!ShowStartupErrorDialog(_apiStartError))
                {
                    ExitThread();
                    return;
                }
            }
```

Note there is no `ShowControlPanel();` call after this block. The Control Panel stays reachable via tray double-click (line ~2046) and the "Show Control Panel" menu item (line ~2019); do not touch those.

- [ ] **Step 2: Build**

```bash
powershell -NoProfile -File build.ps1 -OutputPath "C:\dvb\out\duvc-api.exe"
```

Expected: `Built C:\dvb\out\duvc-api.exe`

- [ ] **Step 3: Verify the panel no longer auto-opens but is still reachable**

```bash
powershell -NoProfile -Command "$env:DUVC_API_KIOSK=$null; & C:\dvb\out\duvc-api.exe app"
```

Expected: tray icon appears, balloon appears, and **no Control Panel window opens**. Then double-click the tray icon — the Control Panel opens. Close it, right-click the icon, choose "Show Control Panel" — it opens again. Exit via the tray menu.

- [ ] **Step 4: Verify the non-kiosk error dialog still appears**

Start the port holder detached, recording its PID:

```bash
powershell -NoProfile -Command "(Start-Process powershell -PassThru -WindowStyle Hidden -ArgumentList '-NoProfile','-File','C:\dvb\out\hold-port.ps1').Id | Set-Content C:\dvb\out\holder.pid; Get-Content C:\dvb\out\holder.pid"
```

Expected: a PID is printed. Now start the app in the foreground:

```bash
powershell -NoProfile -Command "$env:DUVC_API_KIOSK=$null; & C:\dvb\out\duvc-api.exe app"
```

Expected: the modal "The API failed to start on port 3790" dialog appears with Open Control Panel / Exit buttons. Click Exit; the process ends.

- [ ] **Step 5: Verify kiosk mode logs and exits instead of showing a dialog**

With the port still held from Step 4:

```bash
powershell -NoProfile -Command "$env:DUVC_API_KIOSK='1'; & C:\dvb\out\duvc-api.exe app"
```

Expected: **no dialog**; the process exits on its own within a second or two. Confirm the log line (note the log sits beside the exe, not in `%ProgramData%`):

```bash
powershell -NoProfile -Command "Get-Content C:\dvb\out\duvc-api.log -Tail 5"
```

Expected: a line containing `API failed to start in kiosk mode; exiting for watchdog retry`.

Now release the port:

```bash
powershell -NoProfile -Command "Stop-Process -Id (Get-Content C:\dvb\out\holder.pid) -Force; Remove-Item C:\dvb\out\holder.pid"
```

- [ ] **Step 6: Commit**

```bash
git add src/DuvcApi/Program.cs
git commit -m "Never auto-open the Control Panel; suppress startup error dialog on kiosks"
```

---

### Task 3: Widen the tray registration retry schedule

**Files:**
- Modify: `src/DuvcApi/Program.cs:1999-2009` (replace the one-shot `bootRetry` timer) and add a helper method plus a static field to `TrayApp`

**Interfaces:**
- Consumes: `_ui.ShowTrayIcon` from Task 1, and the existing `ReassertNotifyIcon()`.
- Produces: `private void StartTrayRegistrationRetries()` on `TrayApp`.

- [ ] **Step 1: Replace the one-shot boot retry with a bounded schedule**

Replace lines 1999-2009 (the `// Belt-and-suspenders:` comment through `bootRetry.Start();`) with:

```csharp
            // Belt-and-suspenders: if the first Visible above was rejected and no
            // TaskbarCreated fires (e.g. session re-attach), re-assert on a short
            // bounded schedule. Skipped entirely in kiosk mode, where there is no
            // icon to register.
            if (_ui.ShowTrayIcon)
            {
                StartTrayRegistrationRetries();
            }
```

- [ ] **Step 2: Add the retry helper**

Add this static field and method to `TrayApp`, immediately after the `ReassertNotifyIcon()` method:

```csharp
        // Gaps between re-assert attempts, so attempts land at roughly 200 ms,
        // 5 s and 20 s after construction. Shell_NotifyIcon gives no success
        // signal — WinForms swallows a failed NIM_ADD — so we cannot query
        // whether registration took and must simply retry. Three attempts keeps
        // flicker negligible while covering a cold boot where Explorer is not
        // ready at the first attempt.
        private static readonly int[] TrayRetryGapsMs = { 200, 4800, 15000 };

        private void StartTrayRegistrationRetries()
        {
            var index = 0;
            var retry = new System.Windows.Forms.Timer();
            retry.Interval = TrayRetryGapsMs[0];
            retry.Tick += (s, e) =>
            {
                ReassertNotifyIcon();
                index++;
                if (index >= TrayRetryGapsMs.Length)
                {
                    retry.Stop();
                    retry.Dispose();
                    return;
                }
                retry.Interval = TrayRetryGapsMs[index];
            };
            retry.Start();
        }
```

- [ ] **Step 3: Build**

```bash
powershell -NoProfile -File build.ps1 -OutputPath "C:\dvb\out\duvc-api.exe"
```

Expected: `Built C:\dvb\out\duvc-api.exe`

- [ ] **Step 4: Verify non-kiosk re-asserts run three times and then stop**

The log accumulates across runs, so check a delta rather than an absolute. Record the count first:

```bash
powershell -NoProfile -Command "if (Test-Path C:\dvb\out\duvc-api.log) { (Select-String -Path C:\dvb\out\duvc-api.log -Pattern 're-registered tray icon').Count } else { 0 }"
```

Note that number. Start the app in the foreground and leave it running at least 30 seconds:

```bash
powershell -NoProfile -Command "$env:DUVC_API_KIOSK=$null; & C:\dvb\out\duvc-api.exe app"
```

The icon should stay visible throughout. After 30 s exit via the tray menu, then re-count:

```bash
powershell -NoProfile -Command "(Select-String -Path C:\dvb\out\duvc-api.log -Pattern 're-registered tray icon').Count"
```

Expected: exactly **3 more** than the number you noted — attempts at ~200 ms, ~5 s and ~20 s, after which the timer stops and disposes.

- [ ] **Step 5: Verify kiosk mode logs no re-assert at all**

Re-count as in Step 4 and note the number, then:

```bash
powershell -NoProfile -Command "$env:DUVC_API_KIOSK='1'; & C:\dvb\out\duvc-api.exe app"
```

Leave running ~30 seconds. Expected: no tray icon appears at any point. Stop it and re-count:

```bash
powershell -NoProfile -Command "Get-Process duvc-api -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq 'C:\dvb\out\duvc-api.exe' } | Stop-Process -Force; (Select-String -Path C:\dvb\out\duvc-api.log -Pattern 're-registered tray icon').Count"
```

Expected: **unchanged** from the number you noted — the kiosk guard short-circuits every re-assert, and the retry timer is never started.

- [ ] **Step 6: Verify Explorer restart still restores the icon (non-kiosk)**

Start the app non-kiosk as in Step 4, then restart Explorer:

```bash
powershell -NoProfile -Command "Stop-Process -Name explorer -Force"
```

Expected: Explorer restarts automatically and the tray icon reappears within a few seconds; a new `re-registered tray icon` line is logged. Exit via the tray menu.

- [ ] **Step 7: Commit**

```bash
git add src/DuvcApi/Program.cs
git commit -m "Widen tray icon re-registration to a bounded 200ms/5s/20s schedule"
```

---

### Task 4: Set `DUVC_API_KIOSK` from the kiosk installer

**Files:**
- Modify: `dist/kiosk.ps1.txt:799` (inside the `if ($InstallDuvc)` block)

**Interfaces:**
- Consumes: the `DUVC_API_KIOSK` contract from Task 1.
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Set the variable before the service is installed or started**

In `dist/kiosk.ps1.txt`, immediately after the line `$duvcServiceName = 'DuvcApi'` (line 799) and before the `$service = Get-Service ...` line, insert:

```powershell

        # Kiosk tablets run with no desktop chrome: duvc-api reads this at startup
        # and suppresses its tray icon, balloon, and modal dialogs. Machine scope
        # so the SYSTEM watchdog and the session process it launches via
        # CreateProcessAsUser/CreateEnvironmentBlock both inherit it. Set before
        # the service is installed or started so it takes effect immediately.
        [Environment]::SetEnvironmentVariable('DUVC_API_KIOSK', '1', 'Machine')
        $env:DUVC_API_KIOSK = '1'
```

The `$env:` assignment covers the current script process, which launches `duvc-api.exe install` before the machine-scope value has propagated to it.

- [ ] **Step 2: Validate the script still parses**

```bash
powershell -NoProfile -Command "$e=$null; $null=[System.Management.Automation.Language.Parser]::ParseFile('C:\Users\eriks\Documents\git_local\duvc-api\dist\kiosk.ps1.txt',[ref]$null,[ref]$e); if($e){$e|ForEach-Object{$_.Message}; exit 1}else{'kiosk.ps1.txt parses OK'}"
```

Expected: `kiosk.ps1.txt parses OK`

- [ ] **Step 3: Verify the assignment is positioned before the install call**

```bash
grep -n "DUVC_API_KIOSK\|duvc-api install\|ArgumentList 'install'" dist/kiosk.ps1.txt
```

Expected: both `DUVC_API_KIOSK` lines report line numbers **lower** than the `ArgumentList 'install'` line.

- [ ] **Step 4: Commit**

```bash
git add dist/kiosk.ps1.txt
git commit -m "Kiosk installer sets DUVC_API_KIOSK=1 at machine scope"
```

---

## Post-implementation

The kiosk script change only reaches tablets through a release. Per the release rules, a new tag needs a freshly built `duvc-api.exe` asset plus its `.sha256` sidecar (lowercase hex, two spaces, `duvc-api.exe`, no trailing newline, 78 bytes) — never publish a new tag with a stale exe. Bump `src/DuvcApi/AssemblyInfo.cs` and update the pinned `releases/download/vX.Y.Z` URLs in `dist/kiosk.ps1.txt` as part of cutting that release. That release is **not** part of this plan; confirm with the user before publishing, since it triggers a fleet-wide self-update.
