# Service Watchdog + Manual/Auto Update Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the installed Windows service a watchdog + updater that launches the API into the interactive session and applies updates as SYSTEM, and add a manual "Update" item to the tray menu.

**Architecture:** All C# lives in the single file `src/DuvcApi/Program.cs` (the codebase is a single-file `csc.exe` build — keep that pattern). `AutoUpdater` is refactored into check/download primitives. `DuvcApiService` becomes a watchdog (launches/monitors `duvc-api.exe app` in the active session via `CreateProcessAsUser`) and the updater (downloads as SYSTEM, swaps the binary with backup/rollback). `TrayApp` gets an always-visible "Update" menu item. A Users-writable request file under `%ProgramData%\DuvcApi\` is the IPC between the tray (kiosk user) and the service.

**Tech Stack:** C# / .NET Framework 4.8, `csc.exe` single-file build (`build.ps1`), Win32 P/Invoke (`wtsapi32`, `userenv`, `advapi32`, `kernel32`), Windows Services, WinForms tray.

**Verification approach:** This codebase has no unit-test harness, and the feature is OS-integration code (services, sessions, `CreateProcessAsUser`, binary swap) that cannot be meaningfully unit-tested. Each task is verified by **compiling** (Task step shows the exact command) and the final task runs a **manual integration matrix** on a real Windows session, using the elevated-PowerShell pattern already used in this repo. This is a deliberate, documented deviation from unit-TDD.

**Compile-check command** (used in most tasks — compiles to a temp path so it never collides with a locked `dist\duvc-api.exe`):

```powershell
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
& $csc /nologo /target:winexe /optimize+ /out:"$env:TEMP\duvc-api-check.exe" `
  /resource:bin\duvc-cli.exe,duvc-cli.exe `
  /reference:System.ServiceProcess.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
  /reference:System.Net.Http.dll /reference:System.Net.WebSockets.dll /reference:System.Net.WebSockets.Client.dll `
  /reference:System.Web.Extensions.dll `
  src\DuvcApi\Program.cs src\DuvcApi\AssemblyInfo.cs
"exit code: $LASTEXITCODE"
```
Expected when a task is done: `exit code: 0`.

---

## File Structure

| File | Change |
|------|--------|
| `src/DuvcApi/Program.cs` | All C#: new `Paths` + `UpdateInfo` + `SessionLauncher` classes; `AutoUpdater` refactor; `DuvcApiService` rewrite; `ServiceInstaller` changes; `TrayApp` menu item. |
| `kiosk_setup_script/kiosk10.ps1` | Remove redundant `Set-Service`/`Start-Service` lines from the duvc-api block. |

No new files (single-file build pattern). `build.ps1` unchanged.

---

## Task 1: Add `Paths` + `UpdateInfo`, refactor `AutoUpdater` into primitives

**Files:**
- Modify: `src/DuvcApi/Program.cs` — replace the entire `AutoUpdater` class (currently `internal sealed class AutoUpdater { ... }`, roughly lines 2305–2551), and add two new classes immediately before it.

- [ ] **Step 1: Add the `Paths` helper class**

Insert this immediately *before* the `internal sealed class AutoUpdater` declaration:

```csharp
internal static class Paths
{
    // Users-writable state directory created by `install`. Holds the update
    // request file (IPC) plus the existing log/settings files.
    public static string StateDir
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "DuvcApi");
        }
    }

    public static string RequestFile { get { return Path.Combine(StateDir, "update.request"); } }

    public static string CurrentExe { get { return Process.GetCurrentProcess().MainModule.FileName; } }

    public static string BackupExe(string exePath)
    {
        return Path.Combine(Path.GetDirectoryName(exePath), "duvc-api.bak.exe");
    }

    // The running exe is renamed here during a service-driven update so the new
    // exe can take the canonical path while this process keeps running.
    public static string OldExe(string exePath)
    {
        return Path.Combine(Path.GetDirectoryName(exePath), "duvc-api.old.exe");
    }

    public static string SiblingCli(string exePath)
    {
        return Path.Combine(Path.GetDirectoryName(exePath), "duvc-cli.exe");
    }
}
```

- [ ] **Step 2: Replace the `AutoUpdater` class with the refactored version**

Delete the whole existing `internal sealed class AutoUpdater { ... }` block and replace it with `UpdateInfo` + the new `AutoUpdater`:

```csharp
internal sealed class UpdateInfo
{
    public string Version { get; set; }
    public string ExeUrl { get; set; }
    public string Sha256Url { get; set; }
}

internal sealed class AutoUpdater
{
    private const string ReleasesUrl = "https://api.github.com/repos/eriksp/duvc-api/releases/latest";
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private readonly string _currentVersion;

    public AutoUpdater()
    {
        _currentVersion = Program.GetVersionLabel().TrimStart('v');
    }

    // Last result of CheckForUpdate(): non-null when a newer release exists.
    public UpdateInfo AvailableUpdate { get; private set; }

    // Queries releases/latest. Returns an UpdateInfo when a newer version with a
    // duvc-api.exe asset exists, otherwise null. Never throws; caches the result.
    public UpdateInfo CheckForUpdate()
    {
        try
        {
            string tagName, exeUrl, sha256Url;
            if (!FetchLatestRelease(out tagName, out exeUrl, out sha256Url))
            {
                return AvailableUpdate;
            }

            var latestVersion = tagName.TrimStart('v');
            if (!IsNewer(latestVersion, _currentVersion) || string.IsNullOrEmpty(exeUrl))
            {
                AvailableUpdate = null;
                return null;
            }

            AvailableUpdate = new UpdateInfo
            {
                Version = latestVersion,
                ExeUrl = exeUrl,
                Sha256Url = sha256Url
            };
            Logger.Info(string.Format(CultureInfo.InvariantCulture,
                "Update available: v{0} -> v{1}", _currentVersion, latestVersion));
            return AvailableUpdate;
        }
        catch (Exception ex)
        {
            Logger.Error("Update check failed: " + ex.Message);
            return AvailableUpdate;
        }
    }

    // Downloads the update exe and verifies its SHA-256. Returns the temp path of
    // the verified exe, or null on any failure. Never throws.
    public string DownloadAndVerify(UpdateInfo info)
    {
        if (info == null || string.IsNullOrEmpty(info.ExeUrl)) return null;

        var tempPath = Path.Combine(Path.GetTempPath(),
            "duvc-api.update." + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            DownloadFile(info.ExeUrl, tempPath);

            if (!string.IsNullOrEmpty(info.Sha256Url))
            {
                var expected = DownloadString(info.Sha256Url).Trim().Split(' ')[0];
                var actual = ComputeSha256(tempPath);
                if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Error(string.Format(CultureInfo.InvariantCulture,
                        "SHA256 mismatch: expected {0}, got {1}", expected, actual));
                    TryDelete(tempPath);
                    return null;
                }
                Logger.Info("Update SHA256 verified.");
            }

            return tempPath;
        }
        catch (Exception ex)
        {
            Logger.Error("Update download failed: " + ex.Message);
            TryDelete(tempPath);
            return null;
        }
    }

    // Standalone (no service installed): swap via a temp .cmd that waits for this
    // process to exit, moves the verified exe into place, drops the sibling
    // duvc-cli.exe so it re-extracts, and restarts "app".
    public void ApplyInProcess(string verifiedExePath)
    {
        var exePath = Paths.CurrentExe;
        var batchPath = Path.Combine(Path.GetTempPath(), "duvc-api-update.cmd");

        var script = string.Format(CultureInfo.InvariantCulture,
            "@echo off\r\n:retry\r\ntimeout /t 2 /nobreak >nul\r\n" +
            "move /Y \"{0}\" \"{1}\" >nul 2>&1\r\nif errorlevel 1 goto retry\r\n" +
            "del \"{2}\" >nul 2>&1\r\nstart \"\" \"{1}\" app\r\ndel \"%~f0\"\r\n",
            verifiedExePath, exePath, Paths.SiblingCli(exePath));

        File.WriteAllText(batchPath, script, Encoding.ASCII);

        Logger.Info("Applying update, restarting...");
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c \"" + batchPath + "\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        });

        Application.Exit();
    }

    private bool FetchLatestRelease(out string tagName, out string exeUrl, out string sha256Url)
    {
        tagName = null;
        exeUrl = null;
        sha256Url = null;

        try
        {
            var request = (HttpWebRequest)WebRequest.Create(ReleasesUrl);
            request.Method = "GET";
            request.Timeout = 15000;
            request.UserAgent = "duvc-api/" + _currentVersion;

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream()))
            {
                var body = reader.ReadToEnd();
                var data = Json.Deserialize<Dictionary<string, object>>(body);
                if (data == null) return false;

                if (data.ContainsKey("tag_name"))
                {
                    tagName = data["tag_name"].ToString();
                }

                var assetsObj = data.ContainsKey("assets") ? data["assets"] as ArrayList : null;
                if (assetsObj != null)
                {
                    foreach (var item in assetsObj)
                    {
                        var asset = item as Dictionary<string, object>;
                        if (asset == null) continue;

                        var name = asset.ContainsKey("name") ? asset["name"].ToString() : "";
                        var url = asset.ContainsKey("browser_download_url")
                            ? asset["browser_download_url"].ToString() : "";

                        if (string.Equals(name, "duvc-api.exe", StringComparison.OrdinalIgnoreCase))
                            exeUrl = url;
                        else if (string.Equals(name, "duvc-api.exe.sha256", StringComparison.OrdinalIgnoreCase))
                            sha256Url = url;
                    }
                }

                return !string.IsNullOrEmpty(tagName);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to fetch release info: " + ex.Message);
            return false;
        }
    }

    private static bool IsNewer(string latest, string current)
    {
        Version latestVer, currentVer;
        if (Version.TryParse(latest, out latestVer) && Version.TryParse(current, out currentVer))
        {
            return latestVer > currentVer;
        }
        return false;
    }

    private static void DownloadFile(string url, string targetPath)
    {
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.Timeout = 120000;
        request.UserAgent = "duvc-api";

        using (var response = (HttpWebResponse)request.GetResponse())
        using (var stream = response.GetResponseStream())
        using (var file = File.Create(targetPath))
        {
            stream.CopyTo(file);
        }
    }

    private static string DownloadString(string url)
    {
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.Timeout = 15000;
        request.UserAgent = "duvc-api";

        using (var response = (HttpWebResponse)request.GetResponse())
        using (var reader = new StreamReader(response.GetResponseStream()))
        {
            return reader.ReadToEnd();
        }
    }

    private static string ComputeSha256(string filePath)
    {
        using (var sha = System.Security.Cryptography.SHA256.Create())
        using (var stream = File.OpenRead(filePath))
        {
            var hash = sha.ComputeHash(stream);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
            {
                sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}
```

- [ ] **Step 3: Compile-check**

Run the compile-check command from the plan header.
Expected: `exit code: 0`.
Note: this will *fail* with errors in `TrayApp` (it still calls `_updater.Start()` / `_updater.Stop()`) and in `DuvcApiService`/`ServiceBaseHost` only if those reference removed members — they don't yet, but `TrayApp` does. If you see exactly `'AutoUpdater' does not contain a definition for 'Start'` / `'Stop'`, that is expected and fixed in Task 5. To verify Task 1 in isolation, temporarily comment out lines `_updater.Start();` and the `_updater.Stop();` block in `TrayApp`, confirm `exit code: 0`, then un-comment them (Task 5 replaces them properly). If there are *other* errors, fix them before continuing.

- [ ] **Step 4: Commit**

```bash
git add src/DuvcApi/Program.cs
git commit -m "Refactor AutoUpdater into check/download primitives; add Paths helper"
```

---

## Task 2: Add `SessionLauncher` (WTS / CreateProcessAsUser interop)

**Files:**
- Modify: `src/DuvcApi/Program.cs` — add a new `internal static class SessionLauncher` immediately before `internal static class Paths`.

- [ ] **Step 1: Add the `SessionLauncher` class**

```csharp
// Launches a process into the active interactive console session from a
// Session 0 service (LocalSystem holds SE_TCB_NAME, required by WTSQueryUserToken).
internal static class SessionLauncher
{
    private const uint INVALID_SESSION = 0xFFFFFFFF;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NEW_CONSOLE = 0x00000010;
    private const uint TOKEN_ALL_ACCESS = 0xF01FF;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess,
        IntPtr lpTokenAttributes, int impersonationLevel, int tokenType, out IntPtr phNewToken);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr hToken, string lpApplicationName,
        string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    // True if any process with the given base name (no extension) runs in the
    // active console session. Excludes Session 0 (the service's own process).
    public static bool IsRunningInActiveSession(string processName)
    {
        uint session = WTSGetActiveConsoleSessionId();
        if (session == INVALID_SESSION || session == 0) return false;

        foreach (var p in Process.GetProcessesByName(processName))
        {
            try
            {
                if ((uint)p.SessionId == session) return true;
            }
            catch { }
            finally { p.Dispose(); }
        }
        return false;
    }

    // Launches "<exePath> <args>" in the active console session. Returns false if
    // there is no logged-on console user yet, or on any failure (logged).
    public static bool TryLaunchInActiveSession(string exePath, string args)
    {
        uint session = WTSGetActiveConsoleSessionId();
        if (session == INVALID_SESSION || session == 0)
        {
            return false;
        }

        IntPtr userToken = IntPtr.Zero;
        IntPtr primaryToken = IntPtr.Zero;
        IntPtr envBlock = IntPtr.Zero;
        try
        {
            if (!WTSQueryUserToken(session, out userToken))
            {
                // No interactive user logged on yet — caller retries later.
                return false;
            }

            if (!DuplicateTokenEx(userToken, TOKEN_ALL_ACCESS, IntPtr.Zero,
                    SecurityImpersonation, TokenPrimary, out primaryToken))
            {
                Logger.Error("DuplicateTokenEx failed: " + Marshal.GetLastWin32Error());
                return false;
            }

            CreateEnvironmentBlock(out envBlock, primaryToken, false);

            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(si);
            si.lpDesktop = "winsta0\\default";

            PROCESS_INFORMATION pi;
            var commandLine = "\"" + exePath + "\" " + args;
            bool ok = CreateProcessAsUser(primaryToken, exePath, commandLine,
                IntPtr.Zero, IntPtr.Zero, false,
                CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE,
                envBlock, Path.GetDirectoryName(exePath), ref si, out pi);

            if (!ok)
            {
                Logger.Error("CreateProcessAsUser failed: " + Marshal.GetLastWin32Error());
                return false;
            }

            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("TryLaunchInActiveSession failed: " + ex.Message);
            return false;
        }
        finally
        {
            if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
            if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
    }
}
```

- [ ] **Step 2: Confirm `System.Runtime.InteropServices` is imported**

The file already has `using System.Runtime.InteropServices;` near the top (line ~13). If it is missing, add it. No build.ps1 change is needed — `kernel32`/`advapi32`/`wtsapi32`/`userenv` are resolved at runtime by P/Invoke, not at compile time.

- [ ] **Step 3: Compile-check**

Run the compile-check command. Expected: `exit code: 0` (with the same temporary `TrayApp` comment-out from Task 1 still applied, or just confirm no *new* errors beyond the known `_updater.Start/Stop` ones).

- [ ] **Step 4: Commit**

```bash
git add src/DuvcApi/Program.cs
git commit -m "Add SessionLauncher for launching the app into the active session"
```

---

## Task 3: Rewrite `DuvcApiService` as watchdog + updater

**Files:**
- Modify: `src/DuvcApi/Program.cs` — replace the entire `internal sealed class DuvcApiService : ServiceBase { ... }` block (roughly lines 166–188).

- [ ] **Step 1: Replace the `DuvcApiService` class**

```csharp
internal sealed class DuvcApiService : ServiceBase
{
    private const string AppProcessName = "duvc-api";
    private static readonly TimeSpan WatchdogTick = TimeSpan.FromSeconds(10);

    private Thread _worker;
    private volatile bool _running;
    private readonly AutoUpdater _updater = new AutoUpdater();
    private DateTime _lastUpdateCheckUtc = DateTime.MinValue;

    public DuvcApiService(string serviceName, string displayName)
    {
        ServiceName = serviceName;
        CanStop = true;
        CanPauseAndContinue = false;
        CanHandleSessionChangeEvent = true;
        AutoLog = true;
    }

    protected override void OnStart(string[] args)
    {
        // Clean up a leftover renamed exe from a previous update, if it is no
        // longer locked.
        TryDelete(Paths.OldExe(Paths.CurrentExe));

        _running = true;
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "DuvcApiWatchdog" };
        _worker.Start();
    }

    protected override void OnStop()
    {
        _running = false;
        if (_worker != null)
        {
            _worker.Join(5000);
        }
    }

    protected override void OnSessionChange(SessionChangeDescription change)
    {
        if (change.Reason == SessionChangeReason.SessionLogon ||
            change.Reason == SessionChangeReason.ConsoleConnect ||
            change.Reason == SessionChangeReason.SessionUnlock)
        {
            EnsureAppRunning();
        }
    }

    private void WorkerLoop()
    {
        while (_running)
        {
            try
            {
                EnsureAppRunning();

                if (File.Exists(Paths.RequestFile))
                {
                    TryDelete(Paths.RequestFile);
                    Logger.Info("Manual update request received.");
                    RunUpdate();
                }
                else if (DateTime.UtcNow - _lastUpdateCheckUtc >= GetUpdateInterval())
                {
                    _lastUpdateCheckUtc = DateTime.UtcNow;
                    RunUpdate();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Watchdog loop error: " + ex.Message);
            }

            // Sleep WatchdogTick in 500 ms slices so OnStop is responsive.
            for (int i = 0; i < WatchdogTick.TotalMilliseconds / 500 && _running; i++)
            {
                Thread.Sleep(500);
            }
        }
    }

    // Launches "app" into the active console session if no instance is running
    // there. Safe to call repeatedly.
    private static void EnsureAppRunning()
    {
        try
        {
            if (SessionLauncher.IsRunningInActiveSession(AppProcessName))
            {
                return;
            }
            if (SessionLauncher.TryLaunchInActiveSession(Paths.CurrentExe, "app"))
            {
                Logger.Info("Watchdog launched app in the active session.");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("EnsureAppRunning failed: " + ex.Message);
        }
    }

    // Checks for an update and, if found, applies it as SYSTEM with backup and
    // health-check rollback. Never throws.
    private void RunUpdate()
    {
        try
        {
            var info = _updater.CheckForUpdate();
            if (info == null)
            {
                return;
            }

            var verified = _updater.DownloadAndVerify(info);
            if (verified == null)
            {
                return;
            }

            var exe = Paths.CurrentExe;
            var backup = Paths.BackupExe(exe);
            var old = Paths.OldExe(exe);
            var cli = Paths.SiblingCli(exe);

            // Back up the current canonical exe (copy from a running image is allowed).
            File.Copy(exe, backup, true);

            // Rename the running exe out of the way. Renaming a running image is
            // allowed on Windows; the service and any app process keep running
            // from the renamed file. This frees the canonical path.
            TryDelete(old);
            File.Move(exe, old);

            // Place the new exe at the canonical path and drop the sibling CLI so
            // the new exe re-extracts the (possibly updated) embedded duvc-cli.exe.
            File.Copy(verified, exe, true);
            TryDelete(cli);
            TryDelete(verified);

            // Restart the app from the new exe.
            StopApp();
            SessionLauncher.TryLaunchInActiveSession(exe, "app");
            Logger.Info("Update applied: v" + info.Version);

            // Rollback if the new version never reports healthy.
            if (!WaitForHealthy(TimeSpan.FromSeconds(30)))
            {
                Logger.Error("New version unhealthy; rolling back to backup.");
                StopApp();
                File.Copy(backup, exe, true);
                TryDelete(cli);
                SessionLauncher.TryLaunchInActiveSession(exe, "app");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Update failed: " + ex.Message);
            // Best-effort recovery: if the canonical exe is missing/broken, restore
            // the backup so the watchdog can relaunch something working.
            try
            {
                var exe = Paths.CurrentExe;
                var backup = Paths.BackupExe(exe);
                if (File.Exists(backup) && !File.Exists(exe))
                {
                    File.Copy(backup, exe, true);
                    SessionLauncher.TryLaunchInActiveSession(exe, "app");
                }
            }
            catch { }
        }
    }

    // Kills every duvc-api process except this service process.
    private static void StopApp()
    {
        var selfId = Process.GetCurrentProcess().Id;
        foreach (var p in Process.GetProcessesByName(AppProcessName))
        {
            try
            {
                if (p.Id == selfId) continue;
                p.Kill();
                p.WaitForExit(5000);
            }
            catch { }
            finally { p.Dispose(); }
        }
    }

    private static bool WaitForHealthy(TimeSpan timeout)
    {
        var url = string.Format(CultureInfo.InvariantCulture,
            "http://127.0.0.1:{0}/health", Program.GetPort());
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = 3000;
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    if (resp.StatusCode == HttpStatusCode.OK)
                    {
                        return true;
                    }
                }
            }
            catch { }
            Thread.Sleep(2000);
        }
        return false;
    }

    private static TimeSpan GetUpdateInterval()
    {
        var env = Environment.GetEnvironmentVariable("DUVC_API_UPDATE_INTERVAL");
        int minutes;
        if (int.TryParse(env, NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes)
            && minutes > 0)
        {
            return TimeSpan.FromMinutes(minutes);
        }
        return TimeSpan.FromMinutes(60);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}
```

- [ ] **Step 2: Compile-check**

Run the compile-check command. Expected: `exit code: 0` apart from the known `TrayApp` `_updater.Start/Stop` errors (fixed in Task 5). Fix any other errors.

- [ ] **Step 3: Commit**

```bash
git add src/DuvcApi/Program.cs
git commit -m "Rewrite DuvcApiService as a session watchdog and SYSTEM updater"
```

---

## Task 4: Update `ServiceInstaller` — auto-start, start service, state dir, drop Run key

**Files:**
- Modify: `src/DuvcApi/Program.cs` — `ServiceInstaller.Install`, `ServiceInstaller.InstallAppTask`, `ServiceInstaller.UninstallAppTask` (roughly lines 1396–1538).

- [ ] **Step 1: Rewrite `Install` to use auto-start, start the service, and create the state dir**

Replace the body of `public static int Install(string serviceName, string displayName)` with:

```csharp
public static int Install(string serviceName, string displayName)
{
    var exePath = Process.GetCurrentProcess().MainModule.FileName;
    var port = Program.GetPort();

    // Register URL ACL so the non-admin kiosk user can bind HttpListener.
    RegisterUrlAcl(port);

    // Create the Users-writable state directory used for logs/settings and the
    // update-request IPC file.
    EnsureStateDir();

    var status = ServiceStatusHelper.GetStatus(serviceName);
    if (status.IsInstalled)
    {
        if (status.IsRunning)
        {
            RunSc(string.Format(CultureInfo.InvariantCulture, "stop {0}", serviceName));
        }
        RunSc(string.Format(CultureInfo.InvariantCulture,
            "config {0} start= auto", serviceName));
    }
    else
    {
        var binPath = string.Format(CultureInfo.InvariantCulture, "\"{0}\" service", exePath);

        var createResult = RunSc(string.Format(CultureInfo.InvariantCulture,
            "create {0} binPath= \"{1}\" start= auto DisplayName= \"{2}\"",
            serviceName, binPath, displayName));
        if (createResult != 0)
        {
            return createResult;
        }

        RunSc(string.Format(CultureInfo.InvariantCulture,
            "description {0} \"Cellari Camera Control API watchdog and updater\"", serviceName));
    }

    // Auto-restart the service on failure (5s, 10s, 30s delays).
    ConfigureRecovery(serviceName);

    // The service is the watchdog: it launches and monitors "duvc-api.exe app"
    // in the interactive session, so the legacy Run key is no longer used.
    RemoveLegacyAppTask();

    // Start the service now so provisioning does not require a reboot. The
    // service's watchdog then launches the app in the active session.
    RunSc(string.Format(CultureInfo.InvariantCulture, "start {0}", serviceName));

    Console.WriteLine("Installed. The DuvcApi service will keep the API running and up to date.");
    return 0;
}
```

- [ ] **Step 2: Add `EnsureStateDir` and replace `InstallAppTask`/`UninstallAppTask` with `RemoveLegacyAppTask`**

Delete the existing `InstallAppTask`, `UninstallAppTask`, and `TryStartAppNow` methods. Replace them with:

```csharp
private static void EnsureStateDir()
{
    try
    {
        var dir = Paths.StateDir;
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        // Grant BUILTIN\Users Modify so the kiosk user can write update.request.
        // S-1-5-32-545 = BUILTIN\Users (locale-independent).
        var psi = new ProcessStartInfo
        {
            FileName = "icacls.exe",
            Arguments = string.Format(CultureInfo.InvariantCulture,
                "\"{0}\" /grant *S-1-5-32-545:(OI)(CI)M", dir),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using (var p = Process.Start(psi))
        {
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Failed to prepare state directory: " + ex.Message);
    }
}

// Removes the legacy HKLM\...\Run value and the legacy scheduled task left by
// older installs. The service watchdog now owns the app lifecycle.
private static void RemoveLegacyAppTask()
{
    try
    {
        using (var key = Registry.LocalMachine.OpenSubKey(RunRegistryPath, writable: true))
        {
            if (key != null && key.GetValue(RunValueName) != null)
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
    }
    catch
    {
        // ignore registry cleanup errors
    }

    RemoveLegacyScheduledTask();
}
```

- [ ] **Step 3: Update `Uninstall` to call `RemoveLegacyAppTask`**

In `public static int Uninstall(string serviceName)`, replace the call to `UninstallAppTask();` with `RemoveLegacyAppTask();`. The method becomes:

```csharp
public static int Uninstall(string serviceName)
{
    RunSc(string.Format(CultureInfo.InvariantCulture, "stop {0}", serviceName));
    RemoveLegacyAppTask();
    RemoveUrlAcl(Program.GetPort());
    return RunSc(string.Format(CultureInfo.InvariantCulture, "delete {0}", serviceName));
}
```

- [ ] **Step 4: Compile-check**

Run the compile-check command. Expected: `exit code: 0` apart from the known `TrayApp` `_updater.Start/Stop` errors. Note: `TrayApp` also calls `RunElevated("install")` and references `_installServiceItem` — those are unaffected. If the compiler reports `TryStartAppNow` or `InstallAppTask` still referenced anywhere other than where you deleted them, fix those call sites.

- [ ] **Step 5: Commit**

```bash
git add src/DuvcApi/Program.cs
git commit -m "Install: auto-start service, create state dir, drop Run key"
```

---

## Task 5: `TrayApp` — manual "Update" menu item + check-only updater

**Files:**
- Modify: `src/DuvcApi/Program.cs` — `TrayApp` class: field declarations (~line 1620), constructor menu block (~lines 1660–1707), `UpdateStatus` (~line 1760), `ExitThreadCore` (~line 1830).

- [ ] **Step 1: Add fields**

Next to the existing `private AutoUpdater _updater;` field declaration in `TrayApp`, add:

```csharp
private ToolStripMenuItem _updateItem;
private System.Threading.Timer _updateCheckTimer;
private volatile bool _firstUpdateCheckDone;
```

- [ ] **Step 2: Add the menu item in the constructor**

In the constructor, where the menu items are created (after `_uninstallServiceItem` is created and before `var exit = new ToolStripMenuItem("Exit");`), add:

```csharp
_updateItem = new ToolStripMenuItem("Checking for updates…") { Enabled = false };
_updateItem.Click += (sender, args) => OnUpdateClicked();
```

Then add it to the menu — after `menu.Items.Add(_uninstallServiceItem);` and before `menu.Items.Add(exit);`, insert:

```csharp
menu.Items.Add(new ToolStripSeparator());
menu.Items.Add(_updateItem);
```

- [ ] **Step 3: Replace the `_updater.Start()` block with a check-only timer**

Find these two lines near the end of the constructor:

```csharp
_updater = new AutoUpdater();
_updater.Start();
```

Replace them with:

```csharp
_updater = new AutoUpdater();
// Check-only polling for the tray menu: first check after 60 s, then hourly.
// The installed service (not this process) performs auto-apply.
_updateCheckTimer = new System.Threading.Timer(
    OnUpdateCheckTick, null, TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(60));
```

- [ ] **Step 4: Add the timer callback, the menu refresh, and the click handler**

Add these three methods to `TrayApp` (anywhere among its private methods, e.g. just after `UpdateServiceMenu`):

```csharp
private void OnUpdateCheckTick(object state)
{
    try
    {
        _updater.CheckForUpdate();
    }
    catch (Exception ex)
    {
        Logger.Error("Tray update check failed: " + ex.Message);
    }
    finally
    {
        _firstUpdateCheckDone = true;
    }
}

private void UpdateUpdateMenu()
{
    if (_updateItem == null) return;

    var info = _updater != null ? _updater.AvailableUpdate : null;
    if (!_firstUpdateCheckDone)
    {
        _updateItem.Text = "Checking for updates…";
        _updateItem.Enabled = false;
    }
    else if (info == null)
    {
        _updateItem.Text = "Up to date";
        _updateItem.Enabled = false;
    }
    else
    {
        _updateItem.Text = "Update to v" + info.Version;
        _updateItem.Enabled = true;
    }
}

private void OnUpdateClicked()
{
    var info = _updater != null ? _updater.AvailableUpdate : null;
    if (info == null) return;

    bool serviceInstalled = false;
    try
    {
        serviceInstalled = ServiceStatusHelper.GetStatus(Program.ServiceNameConst).IsInstalled;
    }
    catch { }

    if (serviceInstalled)
    {
        // Delegate to the SYSTEM service via the request file — the kiosk user
        // cannot overwrite the exe itself.
        try
        {
            Directory.CreateDirectory(Paths.StateDir);
            File.WriteAllText(Paths.RequestFile,
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            _notifyIcon.BalloonTipTitle = Program.AppTitle;
            _notifyIcon.BalloonTipText = "Update to v" + info.Version + " requested…";
            _notifyIcon.ShowBalloonTip(3000);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to write update request: " + ex.Message);
        }
        return;
    }

    // Standalone (no service): download + apply in-process on a background thread
    // so the UI thread is not blocked.
    _notifyIcon.BalloonTipTitle = Program.AppTitle;
    _notifyIcon.BalloonTipText = "Downloading update v" + info.Version + "…";
    _notifyIcon.ShowBalloonTip(3000);

    var worker = new Thread(() =>
    {
        try
        {
            var verified = _updater.DownloadAndVerify(info);
            if (verified != null)
            {
                _updater.ApplyInProcess(verified);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Manual update failed: " + ex.Message);
        }
    })
    { IsBackground = true, Name = "DuvcApiManualUpdate" };
    worker.Start();
}
```

- [ ] **Step 5: Call `UpdateUpdateMenu()` from `UpdateStatus()`**

In `UpdateStatus()`, after the existing `UpdateServiceMenu(status);` line, add:

```csharp
UpdateUpdateMenu();
```

- [ ] **Step 6: Replace the `_updater.Stop()` block in `ExitThreadCore`**

Find this block in `ExitThreadCore`:

```csharp
if (_updater != null)
{
    _updater.Stop();
}
```

Replace it with:

```csharp
if (_updateCheckTimer != null)
{
    _updateCheckTimer.Dispose();
    _updateCheckTimer = null;
}
```

(If you temporarily commented out `_updater.Start()` / `_updater.Stop()` during Tasks 1–4, make sure the un-commented code now matches Steps 3 and 6 exactly.)

- [ ] **Step 7: Compile-check**

Run the compile-check command. Expected: `exit code: 0` with **no** errors now.

- [ ] **Step 8: Full build**

First make sure no `duvc-api.exe` is running and locking `dist\` (`Get-Process duvc-api`; if running and not killable, see Task 7's cleanup). Then:

```powershell
.\build.ps1
```
Expected: `Built ...\dist\duvc-api.exe` and `$LASTEXITCODE` is 0. Confirm `dist\duvc-api.exe` exists and `(Get-Item dist\duvc-api.exe).VersionInfo.ProductVersion` is `1.6.0`.

- [ ] **Step 9: Commit**

```bash
git add src/DuvcApi/Program.cs
git commit -m "Add manual Update item to the tray menu"
```

---

## Task 6: `kiosk10.ps1` — remove redundant service lines

**Files:**
- Modify: `kiosk_setup_script/kiosk10.ps1` — the `--- Camera Control API (duvc-api) ---` block.

- [ ] **Step 1: Remove the redundant `Set-Service` / `Start-Service` lines**

In the duvc-api block, find:

```powershell
        try { Set-Service -Name $duvcServiceName -StartupType Automatic } catch {}
        try { Start-Service -Name $duvcServiceName -ErrorAction SilentlyContinue } catch {}
```

Delete both lines. `duvc-api.exe install` now creates the service as `start= auto` and starts it itself, so these are redundant. Leave the rest of the block (download, SHA-256 verify, `install`, the health check) unchanged.

- [ ] **Step 2: Syntax-check the script**

```powershell
$e=$null
[System.Management.Automation.Language.Parser]::ParseFile(
  (Resolve-Path 'kiosk_setup_script\kiosk10.ps1'), [ref]$null, [ref]$e) | Out-Null
if ($e) { "PARSE ERRORS: " + ($e -join '; ') } else { "syntax OK" }
```
Expected: `syntax OK`.

- [ ] **Step 3: Commit**

```bash
git add kiosk_setup_script/kiosk10.ps1
git commit -m "kiosk10: drop redundant service start (install now auto-starts it)"
```

---

## Task 7: Full build + manual integration test

**Files:** none (verification only).

This task has no automated tests — it is the manual integration matrix from the spec. Run it on a real Windows machine in an **elevated** PowerShell. Each check below is a checkbox.

- [ ] **Step 1: Clean any prior install and build**

Elevated PowerShell:
```powershell
Get-Process duvc-api -ErrorAction SilentlyContinue | Stop-Process -Force
sc.exe delete DuvcApi 2>$null
Remove-Item 'C:\ProgramData\DuvcApi' -Recurse -Force -ErrorAction SilentlyContinue
cd C:\Users\eriks\Documents\git_local\duvc-api
.\build.ps1
```
Expected: `Built ...\dist\duvc-api.exe`, exit code 0.

- [ ] **Step 2: Check 1 — fresh install launches the app into the session**

```powershell
.\dist\duvc-api.exe install
Start-Sleep 8
Get-Service DuvcApi | Format-List Name,Status,StartType
Get-Process duvc-api | Select-Object Id,SessionId
(Invoke-WebRequest http://127.0.0.1:3790/health -UseBasicParsing).Content
```
Expected: service `Running` / `Automatic`; a `duvc-api` process in a **non-zero** SessionId; `/health` returns JSON with `"appVersion":"v1.6.0"` and `"cameraFound":true`.

- [ ] **Step 3: Check 2 — watchdog restarts the app after a crash**

```powershell
Get-Process duvc-api | Where-Object SessionId -ne 0 | Stop-Process -Force
Start-Sleep 15
Get-Process duvc-api | Select-Object Id,SessionId
```
Expected: a new `duvc-api` process in a non-zero session within ~15 s.

- [ ] **Step 4: Check 3 — survives reboot with no admin pre-login**

Reboot the machine, let the kiosk/user auto-logon complete, then (elevated):
```powershell
Get-Service DuvcApi | Select-Object Status
Get-Process duvc-api | Select-Object Id,SessionId
(Invoke-WebRequest http://127.0.0.1:3790/health -UseBasicParsing).Content
```
Expected: service `Running`; one app process in the user session; `/health` healthy; no port-conflict error dialog.

- [ ] **Step 5: Check 4 — service-driven auto-update**

Temporarily set a short interval and point at a newer test release (or use the real one if a newer version is published):
```powershell
[Environment]::SetEnvironmentVariable('DUVC_API_UPDATE_INTERVAL','1','Machine')
Restart-Service DuvcApi
# Wait ~90 s, then:
(Invoke-WebRequest http://127.0.0.1:3790/health -UseBasicParsing).Content
Get-ChildItem 'C:\Kiosk\duvc-api*.exe' | Select-Object Name,Length
```
Expected: if a newer release exists, `/health` shows the new version; `duvc-api.bak.exe` and `duvc-api.old.exe` are present next to `duvc-api.exe`. Reset afterwards: `[Environment]::SetEnvironmentVariable('DUVC_API_UPDATE_INTERVAL',$null,'Machine')`.

- [ ] **Step 6: Check 5 — manual update via the tray (service installed)**

With the service installed, left-click the tray icon → click the "Update to v…" item (only enabled if a newer release exists). 
Expected: a balloon tip "Update to v… requested"; `C:\ProgramData\DuvcApi\update.request` appears briefly then is deleted by the service; within ~10–20 s the app restarts on the new version (`/health`).

- [ ] **Step 7: Check 6 — manual update standalone (no service)**

```powershell
.\dist\duvc-api.exe uninstall
.\dist\duvc-api.exe app
```
With a newer release available, click "Update to v…" in the tray.
Expected: balloon "Downloading update v…"; the process exits and restarts on the new version via the temp `.cmd`.

- [ ] **Step 8: Check 7 — rollback on unhealthy update**

This requires a deliberately-broken test release (an exe that exits immediately / never serves `/health`). With that as the latest release and the service installed, trigger an update (request file or short interval).
Expected: the log shows `New version unhealthy; rolling back to backup.`; `/health` returns to the previous working version.

- [ ] **Step 9: Check 8 — uninstall is clean**

```powershell
.\dist\duvc-api.exe uninstall
Get-Service DuvcApi -ErrorAction SilentlyContinue
(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).CellariCameraControl
Get-Process duvc-api -ErrorAction SilentlyContinue
```
Expected: service gone; Run value gone; no leftover processes.

- [ ] **Step 10: Record results**

If every check passes, the feature is complete. If a check fails, file the specific failure and fix it in `Program.cs` before proceeding to a release. (Cutting a new release is a separate, user-approved step — not part of this plan.)

---

## Self-review notes

- **Spec coverage:** C1 → Task 1; C2 → Tasks 2+3; C3 → Task 5; C4 → Tasks 4 (state dir) + 5 (write) + 3 (read); C5 → Task 4; C6 → Task 6; testing matrix → Task 7. All spec sections are covered.
- **Running-exe swap:** the spec's "overwrite `duvc-api.exe`" is implemented as rename-then-copy (`File.Move` the running image to `duvc-api.old.exe`, then `File.Copy` the new exe to the canonical path) because Windows forbids overwriting a running image in place. `OnStart` cleans up `duvc-api.old.exe`.
- **Type consistency:** `UpdateInfo` (`Version`/`ExeUrl`/`Sha256Url`), `Paths` (`StateDir`/`RequestFile`/`CurrentExe`/`BackupExe`/`OldExe`/`SiblingCli`), `SessionLauncher` (`IsRunningInActiveSession`/`TryLaunchInActiveSession`), `AutoUpdater` (`CheckForUpdate`/`AvailableUpdate`/`DownloadAndVerify`/`ApplyInProcess`) are used consistently across Tasks 1–5.
