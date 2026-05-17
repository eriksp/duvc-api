# Control Panel dialog — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a Control Panel dialog that surfaces app/service/watchdog health, current and latest version with check + apply update, installed paths with Explorer links, install/uninstall service buttons, and an About box with repo links. Wire it from the tray menu (and tray double-click). Replace the `dist/assets/cellari_logo.svg` ikon as the embedded app icon.

**Architecture:** Single new `ControlPanelForm : Form` plus an `EmbeddedAssets` helper added to `src/DuvcApi/Program.cs`. Modeless, single-instance, auto-refresh every 3 s while visible. Reuses existing `TrayApp.RunElevated`, `AutoUpdater.AvailableUpdate`, `StatusClient.Check`, and `ServiceStatusHelper.GetStatus`. SVG is pre-rendered to PNG (256) + ICO (multi-size) once and embedded by `build.ps1` via `csc.exe /resource:`.

**Tech Stack:** C# 7 / .NET Framework 4.8, WinForms (`System.Windows.Forms`), single-file build via `csc.exe`, Inkscape (build-time, optional, only when SVG changes).

**Spec:** `docs/superpowers/specs/2026-05-16-control-panel-dialog-design.md`

**Notes for the implementer:**

- The project has **no automated test harness**. Verification is `build.ps1` exit 0 plus the manual checks in Task 8. Do NOT introduce a test framework; follow the existing contract.
- All new code goes in `src/DuvcApi/Program.cs` to preserve the single-file build.
- Service name is `Program.ServiceNameConst` (which is `"DuvcApi"`), not `"DuvcApiService"`.
- Use the existing `Paths.CurrentExe`, `Paths.StateDir`, `ServiceStatusHelper`, `StatusClient`, `AutoUpdater`, `Logger`. Do not duplicate.
- All commits should be small and verified by `powershell -ExecutionPolicy Bypass -File .\build.ps1` exiting 0.

---

## Task 1: Generate raster icons from the SVG

**Files:**
- Create: `scripts/convert-icon.ps1`
- Create: `dist/assets/cellari_logo_256.png` (generated)
- Create: `dist/assets/cellari_logo.ico` (generated)
- Read only: `dist/assets/cellari_logo.svg`

- [ ] **Step 1: Create the conversion script**

Write `scripts/convert-icon.ps1`:

```powershell
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$svg  = Join-Path $root "dist\assets\cellari_logo.svg"
$png  = Join-Path $root "dist\assets\cellari_logo_256.png"
$ico  = Join-Path $root "dist\assets\cellari_logo.ico"

if (-not (Test-Path $svg)) { throw "Missing SVG: $svg" }

$inkscape = (Get-Command inkscape -ErrorAction SilentlyContinue)
if (-not $inkscape) {
    throw @"
Inkscape not found on PATH. Install from https://inkscape.org and re-run.
This script only needs to run when dist\assets\cellari_logo.svg changes.
The generated PNG and ICO are committed to git.
"@
}

Write-Host "Rasterising $svg -> $png (256x256)"
& inkscape --export-type=png --export-filename=$png --export-width=256 --export-height=256 $svg | Out-Null
if (-not (Test-Path $png)) { throw "PNG export failed" }

$tmp = New-Item -ItemType Directory -Force -Path (Join-Path $env:TEMP "cellari_logo_ico")
$sizes = 16, 32, 48, 256
$pngFiles = @()
foreach ($size in $sizes) {
    $out = Join-Path $tmp.FullName "logo_$size.png"
    & inkscape --export-type=png --export-filename=$out --export-width=$size --export-height=$size $svg | Out-Null
    if (-not (Test-Path $out)) { throw "PNG export failed for size $size" }
    $pngFiles += $out
}

Write-Host "Packing ICO -> $ico (sizes: $($sizes -join ', '))"
Add-Type -AssemblyName System.Drawing
$icons = @()
foreach ($p in $pngFiles) {
    $bytes = [IO.File]::ReadAllBytes($p)
    $icons += ,@($bytes)
}

# Build ICO container manually (ICONDIR + ICONDIRENTRYs + PNG payloads).
$ms = New-Object IO.MemoryStream
$bw = New-Object IO.BinaryWriter($ms)
$bw.Write([uint16]0)              # reserved
$bw.Write([uint16]1)              # type = 1 (icon)
$bw.Write([uint16]$icons.Count)   # image count

$headerSize = 6 + (16 * $icons.Count)
$offset = $headerSize
for ($i = 0; $i -lt $icons.Count; $i++) {
    $size = $sizes[$i]
    $payload = $icons[$i]
    $w = if ($size -ge 256) { 0 } else { $size }
    $h = if ($size -ge 256) { 0 } else { $size }
    $bw.Write([byte]$w)            # width
    $bw.Write([byte]$h)            # height
    $bw.Write([byte]0)             # palette
    $bw.Write([byte]0)             # reserved
    $bw.Write([uint16]1)           # planes
    $bw.Write([uint16]32)          # bpp
    $bw.Write([uint32]$payload.Length)  # bytes in res
    $bw.Write([uint32]$offset)     # offset to payload
    $offset += $payload.Length
}
foreach ($payload in $icons) {
    $bw.Write($payload)
}
$bw.Flush()
[IO.File]::WriteAllBytes($ico, $ms.ToArray())
$bw.Dispose()
$ms.Dispose()
Remove-Item -Recurse -Force $tmp.FullName

Write-Host "Done. PNG=$png ICO=$ico"
```

- [ ] **Step 2: Run the script and verify outputs**

Run: `powershell -ExecutionPolicy Bypass -File .\scripts\convert-icon.ps1`

Expected: prints `Done. PNG=... ICO=...` and both files exist.

Verify with:
```
Test-Path dist\assets\cellari_logo_256.png
Test-Path dist\assets\cellari_logo.ico
```

Both should be `True`. The PNG should be ~256x256 with transparent background; the ICO should be readable by Windows (right-click → Properties shows multiple sizes).

If Inkscape is not installed, the script fails with a clear instruction — install Inkscape from inkscape.org and re-run.

- [ ] **Step 3: Commit**

```bash
git add scripts/convert-icon.ps1 dist/assets/cellari_logo_256.png dist/assets/cellari_logo.ico
git commit -m "Add Cellari logo rasters (PNG 256, multi-size ICO) generated from SVG"
```

---

## Task 2: Embed the rasters in the build

**Files:**
- Modify: `build.ps1`

- [ ] **Step 1: Add raster validation and resource flags**

Replace the body of `build.ps1` (keep header lines 1-35 unchanged). The full replaced file:

```powershell
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root "src\DuvcApi\Program.cs"
$assemblyInfo = Join-Path $root "src\DuvcApi\AssemblyInfo.cs"
$duvcCli = Join-Path $root "bin\duvc-cli.exe"
$dist = Join-Path $root "dist"
$logoPng = Join-Path $root "dist\assets\cellari_logo_256.png"
$logoIco = Join-Path $root "dist\assets\cellari_logo.ico"

if (-not (Test-Path $src)) {
    throw "Missing source file: $src"
}

if (-not (Test-Path $assemblyInfo)) {
    throw "Missing AssemblyInfo.cs: $assemblyInfo"
}

if (-not (Test-Path $duvcCli)) {
    throw "Missing duvc-cli.exe at: $duvcCli"
}

if (-not (Test-Path $logoPng) -or -not (Test-Path $logoIco)) {
    throw "Missing raster icons. Run scripts\convert-icon.ps1 first."
}

if (-not (Test-Path $dist)) {
    New-Item -ItemType Directory -Path $dist | Out-Null
}

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
}

if (-not (Test-Path $csc)) {
    throw "csc.exe not found. Install .NET Framework 4.8 developer tools."
}

$output = Join-Path $dist "duvc-api.exe"

& $csc `
    /nologo `
    /target:winexe `
    /optimize+ `
    /win32icon:$logoIco `
    /out:$output `
    /resource:$duvcCli,duvc-cli.exe `
    /resource:$logoPng,cellari_logo_256.png `
    /resource:$logoIco,cellari_logo.ico `
    /reference:System.ServiceProcess.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Drawing.dll `
    /reference:System.Net.Http.dll `
    /reference:System.Net.WebSockets.dll `
    /reference:System.Net.WebSockets.Client.dll `
    /reference:System.Web.Extensions.dll `
    $src `
    $assemblyInfo

Write-Host "Built $output"
```

Two changes vs the original: the validation block for `$logoPng`/`$logoIco`, and three new flags on `csc.exe` (`/win32icon`, plus two `/resource:`). `/win32icon` makes Explorer show the new icon for `duvc-api.exe`.

- [ ] **Step 2: Build and verify resources are embedded**

Run: `powershell -ExecutionPolicy Bypass -File .\build.ps1`

Expected: `Built C:\...\dist\duvc-api.exe` with exit 0.

Quick smoke that resources made it in:
```powershell
$bytes = [IO.File]::ReadAllBytes("dist\duvc-api.exe")
$text  = [Text.Encoding]::ASCII.GetString($bytes)
$text.Contains("cellari_logo_256.png")   # should be True
$text.Contains("cellari_logo.ico")       # should be True
```

Also verify Explorer shows the new icon for `dist\duvc-api.exe`.

- [ ] **Step 3: Commit**

```bash
git add build.ps1
git commit -m "build: embed Cellari logo PNG + ICO and set Win32 icon"
```

---

## Task 3: Add `EmbeddedAssets` helper + `Paths.LogFile`

**Files:**
- Modify: `src/DuvcApi/Program.cs`

- [ ] **Step 1: Expose the log path on `Paths`**

In `src/DuvcApi/Program.cs`, find the `Paths` class (around line 2797). Add a new property after `StateDir` and before `RequestFile`:

```csharp
public static string LogFile { get { return Path.Combine(StateDir, "duvc-api.log"); } }
```

Then change `Logger.LogPath` (around line 2572) from the inline `Path.Combine(...)` to reuse the new property. Replace:

```csharp
private static readonly string LogPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "DuvcApi",
    "duvc-api.log");
```

with:

```csharp
private static readonly string LogPath = Paths.LogFile;
```

This keeps a single source of truth for the path.

- [ ] **Step 2: Add the `EmbeddedAssets` helper**

Insert this new internal static class near the end of `Program.cs`, just before the closing `}` of the `namespace DuvcApi` block (after `Paths` or wherever sibling helpers live):

```csharp
internal static class EmbeddedAssets
{
    public static Image LoadPng(string resourceName)
    {
        using (var stream = typeof(EmbeddedAssets).Assembly.GetManifestResourceStream(resourceName))
        {
            if (stream == null)
            {
                throw new InvalidOperationException("Embedded resource not found: " + resourceName);
            }
            // Image.FromStream requires the stream to stay open for the life of the Image.
            // Copy into a MemoryStream so the caller can dispose us safely.
            var ms = new MemoryStream();
            stream.CopyTo(ms);
            ms.Position = 0;
            return Image.FromStream(ms);
        }
    }

    public static Icon LoadIcon(string resourceName)
    {
        using (var stream = typeof(EmbeddedAssets).Assembly.GetManifestResourceStream(resourceName))
        {
            if (stream == null)
            {
                throw new InvalidOperationException("Embedded resource not found: " + resourceName);
            }
            return new Icon(stream);
        }
    }
}
```

- [ ] **Step 3: Build to verify compilation**

Run: `powershell -ExecutionPolicy Bypass -File .\build.ps1`

Expected: exit 0, no compile errors.

- [ ] **Step 4: Commit**

```bash
git add src/DuvcApi/Program.cs
git commit -m "Add Paths.LogFile and EmbeddedAssets helper for resource loading"
```

---

## Task 4: Expose health snapshot from `TrayApp`

The existing `TrayApp.UpdateStatus()` calls `StatusClient.Check(port)` and applies the result to the icon, but does not retain it. The Control Panel needs read-only access to the latest result.

**Files:**
- Modify: `src/DuvcApi/Program.cs`

- [ ] **Step 1: Add a snapshot field and a thread-safe accessor**

In the `TrayApp` field block (around line 1845-1865), add:

```csharp
private readonly object _statusLock = new object();
private StatusResult _lastStatus;
private DateTime _lastStatusAt;
```

Add this public-internal property anywhere on `TrayApp`:

```csharp
internal HealthSnapshot GetHealthSnapshot()
{
    lock (_statusLock)
    {
        return new HealthSnapshot
        {
            Status = _lastStatus,
            CheckedAt = _lastStatusAt
        };
    }
}
```

Add the `HealthSnapshot` type adjacent to `StatusResult`:

```csharp
internal sealed class HealthSnapshot
{
    public StatusResult Status { get; set; }   // may be null before first check
    public DateTime CheckedAt { get; set; }    // UTC
}
```

- [ ] **Step 2: Update `UpdateStatus` to cache the result**

Find `private void UpdateStatus()` in `TrayApp` (around line 2002). At the top of the method, after `var status = StatusClient.Check(Program.GetPort());`, add:

```csharp
lock (_statusLock)
{
    _lastStatus = status;
    _lastStatusAt = DateTime.UtcNow;
}
```

Do not change any other behaviour in `UpdateStatus`.

- [ ] **Step 3: Build to verify compilation**

Run: `powershell -ExecutionPolicy Bypass -File .\build.ps1`

Expected: exit 0.

- [ ] **Step 4: Commit**

```bash
git add src/DuvcApi/Program.cs
git commit -m "Expose latest StatusResult snapshot from TrayApp"
```

---

## Task 5: `ControlPanelForm` scaffolding — header, health, service sections

**Files:**
- Modify: `src/DuvcApi/Program.cs`

- [ ] **Step 1: Add the form class skeleton**

Insert the following near the bottom of `Program.cs` (before the closing namespace brace, near `LogForm` or after `TrayApp`). This is the first half of the class — the remaining sections come in Task 6.

```csharp
internal sealed class ControlPanelForm : Form
{
    private static readonly Color OkColor   = Color.FromArgb(0x2E, 0xBC, 0x4F);
    private static readonly Color WarnColor = Color.FromArgb(0xF2, 0xA9, 0x3B);
    private static readonly Color BadColor  = Color.FromArgb(0xD6, 0x45, 0x45);
    private static readonly Color NaColor   = Color.FromArgb(0x9E, 0x9E, 0x9E);

    private readonly TrayApp _tray;
    private readonly AutoUpdater _updater;

    private readonly System.Windows.Forms.Timer _refreshTimer;

    // Section refs we need to update
    private Label _modeLabel;
    private StatusBullet _apiBullet, _watchdogBullet, _serviceBullet;
    private Label _apiText, _watchdogText, _serviceText;
    private Label _serviceStatusLabel;
    private Button _installBtn, _uninstallBtn;
    private Label _currentVerLabel, _latestVerLabel, _updateStatusLabel;
    private Button _checkUpdateBtn, _applyUpdateBtn;
    private LinkLabel _openExeFolderLink, _openStateFolderLink, _openLogFolderLink;
    private Button _showLogBtn;

    private volatile bool _checking;

    public ControlPanelForm(TrayApp tray, AutoUpdater updater)
    {
        _tray = tray;
        _updater = updater;

        SuspendLayout();

        Text = Program.AppTitle + " — Control Panel";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(720, 600);
        AutoScroll = true;
        BackColor = SystemColors.Window;
        Font = new Font("Segoe UI", 9f);

        try { Icon = EmbeddedAssets.LoadIcon("cellari_logo.ico"); }
        catch (Exception ex) { Logger.Error("Control Panel icon load failed: " + ex.Message); }

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16),
            BackColor = SystemColors.Window
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        root.Controls.Add(BuildHeader());
        root.Controls.Add(BuildHealthSection());
        root.Controls.Add(BuildServiceSection());
        root.Controls.Add(BuildUpdateSection());
        root.Controls.Add(BuildPathsSection());
        root.Controls.Add(BuildAboutSection());
        root.Controls.Add(BuildFooter());

        Controls.Add(root);

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _refreshTimer.Tick += (s, e) => RefreshAll();

        Shown += (s, e) => { RefreshAll(); _refreshTimer.Start(); };
        FormClosing += (s, e) => { _refreshTimer.Stop(); };
        Deactivate += (s, e) => _refreshTimer.Stop();
        Activated += (s, e) => { if (Visible) { RefreshAll(); _refreshTimer.Start(); } };

        ResumeLayout(false);
    }

    // -- Header ---------------------------------------------------------
    private Control BuildHeader()
    {
        var panel = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 12)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80f));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        var pic = new PictureBox
        {
            Size = new Size(64, 64),
            SizeMode = PictureBoxSizeMode.Zoom,
            Margin = new Padding(0, 0, 8, 0),
            BackColor = Color.Transparent
        };
        try { pic.Image = EmbeddedAssets.LoadPng("cellari_logo_256.png"); }
        catch (Exception ex) { Logger.Error("Control Panel logo load failed: " + ex.Message); }
        panel.Controls.Add(pic, 0, 0);
        panel.SetRowSpan(pic, 2);

        var title = new Label
        {
            Text = "DUVC API Control Panel",
            Font = new Font("Segoe UI", 14f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 0)
        };
        panel.Controls.Add(title, 1, 0);

        _modeLabel = new Label
        {
            Text = "—",
            ForeColor = SystemColors.GrayText,
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 0)
        };
        panel.Controls.Add(_modeLabel, 1, 1);

        return panel;
    }

    // -- Health ---------------------------------------------------------
    private Control BuildHealthSection()
    {
        var box = NewGroupBox("Health");
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            AutoSize = true,
            Padding = new Padding(8)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        _apiBullet = new StatusBullet();
        _apiText = NewBodyLabel("—");
        _watchdogBullet = new StatusBullet();
        _watchdogText = NewBodyLabel("—");
        _serviceBullet = new StatusBullet();
        _serviceText = NewBodyLabel("—");

        grid.Controls.Add(_apiBullet,    0, 0); grid.Controls.Add(NewBodyLabel("API"),      1, 0); grid.Controls.Add(_apiText,      2, 0);
        grid.Controls.Add(_watchdogBullet, 0, 1); grid.Controls.Add(NewBodyLabel("Watchdog"), 1, 1); grid.Controls.Add(_watchdogText, 2, 1);
        grid.Controls.Add(_serviceBullet,  0, 2); grid.Controls.Add(NewBodyLabel("Service"),  1, 2); grid.Controls.Add(_serviceText,  2, 2);

        box.Controls.Add(grid);
        return box;
    }

    // -- Service --------------------------------------------------------
    private Control BuildServiceSection()
    {
        var box = NewGroupBox("Service");
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoSize = true,
            Padding = new Padding(8)
        };

        _serviceStatusLabel = NewBodyLabel("Status: —");
        grid.Controls.Add(_serviceStatusLabel);

        var btnRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0)
        };
        _installBtn = new Button { Text = "Install Service", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
        _installBtn.Click += (s, e) => _tray.RunElevatedFromControlPanel("install");
        _uninstallBtn = new Button { Text = "Uninstall Service", AutoSize = true, Margin = new Padding(8, 0, 0, 0), Padding = new Padding(8, 2, 8, 2) };
        _uninstallBtn.Click += (s, e) => _tray.RunElevatedFromControlPanel("uninstall");
        btnRow.Controls.Add(_installBtn);
        btnRow.Controls.Add(_uninstallBtn);
        grid.Controls.Add(btnRow);

        box.Controls.Add(grid);
        return box;
    }

    // -- Helpers shared across sections ---------------------------------
    private static GroupBox NewGroupBox(string title)
    {
        return new GroupBox
        {
            Text = title,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 0, 0, 12),
            Padding = new Padding(8, 6, 8, 8),
            Font = new Font("Segoe UI", 9f, FontStyle.Regular)
        };
    }

    private static Label NewBodyLabel(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(0, 4, 8, 4)
        };
    }

    // Bullet, sections Update/Paths/About/Footer, and RefreshAll come in Task 6.
    private Control BuildUpdateSection() { return NewGroupBox("Update (filled in Task 6)"); }
    private Control BuildPathsSection()  { return NewGroupBox("Installed files (filled in Task 6)"); }
    private Control BuildAboutSection()  { return NewGroupBox("About (filled in Task 6)"); }
    private Control BuildFooter()        { return new Panel { Height = 1 }; }
    private void RefreshAll() { /* filled in Task 6 */ }
}

internal sealed class StatusBullet : Label
{
    private Color _fill = Color.FromArgb(0x9E, 0x9E, 0x9E);
    public StatusBullet()
    {
        AutoSize = false;
        Size = new Size(16, 16);
        Margin = new Padding(0, 6, 4, 0);
        BackColor = Color.Transparent;
    }
    public void SetColor(Color c)
    {
        if (_fill == c) return;
        _fill = c;
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using (var brush = new SolidBrush(_fill))
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.FillEllipse(brush, 2, 2, 12, 12);
        }
    }
}
```

- [ ] **Step 2: Add the `TrayApp.RunElevatedFromControlPanel` helper**

The existing `RunElevated(string command)` on `TrayApp` is `private`. Rather than change its visibility, add a thin internal wrapper on `TrayApp`:

```csharp
internal void RunElevatedFromControlPanel(string command)
{
    RunElevated(command);
}
```

Place it next to `RunElevated`.

- [ ] **Step 3: Build to verify compilation**

Run: `powershell -ExecutionPolicy Bypass -File .\build.ps1`

Expected: exit 0. (The form is not wired to the tray yet; it just compiles.)

- [ ] **Step 4: Commit**

```bash
git add src/DuvcApi/Program.cs
git commit -m "Add ControlPanelForm scaffolding with header/health/service sections"
```

---

## Task 6: `ControlPanelForm` — Update, Paths, About, Footer, and RefreshAll

**Files:**
- Modify: `src/DuvcApi/Program.cs`

- [ ] **Step 1: Replace the four placeholder methods**

In `ControlPanelForm`, replace the four placeholder methods from Task 5 (`BuildUpdateSection`, `BuildPathsSection`, `BuildAboutSection`, `BuildFooter`, `RefreshAll`) with the full implementations:

```csharp
// -- Update ---------------------------------------------------------
private Control BuildUpdateSection()
{
    var box = NewGroupBox("Update");
    var grid = new TableLayoutPanel
    {
        Dock = DockStyle.Fill,
        ColumnCount = 1,
        AutoSize = true,
        Padding = new Padding(8)
    };

    var row = new FlowLayoutPanel
    {
        FlowDirection = FlowDirection.LeftToRight,
        AutoSize = true
    };
    _currentVerLabel   = NewBodyLabel("Current: —");
    _latestVerLabel    = NewBodyLabel("    Latest: —");
    _updateStatusLabel = NewBodyLabel("    Status: —");
    row.Controls.Add(_currentVerLabel);
    row.Controls.Add(_latestVerLabel);
    row.Controls.Add(_updateStatusLabel);
    grid.Controls.Add(row);

    var btnRow = new FlowLayoutPanel
    {
        FlowDirection = FlowDirection.LeftToRight,
        AutoSize = true,
        Margin = new Padding(0, 8, 0, 0)
    };
    _checkUpdateBtn = new Button { Text = "Check for updates", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
    _checkUpdateBtn.Click += (s, e) => OnCheckUpdatesClicked();
    _applyUpdateBtn = new Button { Text = "Apply update", AutoSize = true, Margin = new Padding(8, 0, 0, 0), Padding = new Padding(8, 2, 8, 2), Enabled = false };
    _applyUpdateBtn.Click += (s, e) => _tray.OnUpdateClickedFromControlPanel();
    btnRow.Controls.Add(_checkUpdateBtn);
    btnRow.Controls.Add(_applyUpdateBtn);
    grid.Controls.Add(btnRow);

    box.Controls.Add(grid);
    return box;
}

private void OnCheckUpdatesClicked()
{
    if (_checking) return;
    _checking = true;
    _checkUpdateBtn.Enabled = false;
    _updateStatusLabel.Text = "    Status: Checking…";

    System.Threading.Tasks.Task.Run(() =>
    {
        Exception error = null;
        try { _updater.CheckForUpdate(); }
        catch (Exception ex) { error = ex; }

        BeginInvoke(new Action(() =>
        {
            _checking = false;
            _checkUpdateBtn.Enabled = true;
            if (error != null)
            {
                var msg = error.Message ?? "";
                if (msg.Length > 80) msg = msg.Substring(0, 80) + "…";
                _updateStatusLabel.Text = "    Status: Error: " + msg;
            }
            RefreshAll();
        }));
    });
}

// -- Installed files ------------------------------------------------
private Control BuildPathsSection()
{
    var box = NewGroupBox("Installed files");
    var grid = new TableLayoutPanel
    {
        Dock = DockStyle.Fill,
        ColumnCount = 3,
        AutoSize = true,
        Padding = new Padding(8)
    };
    grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100f));
    grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
    grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

    var monoFont = new Font("Consolas", 9f);

    grid.Controls.Add(NewBodyLabel("Executable:"), 0, 0);
    grid.Controls.Add(new Label { Text = Paths.CurrentExe, Font = monoFont, AutoSize = true, Margin = new Padding(0, 4, 8, 4) }, 1, 0);
    _openExeFolderLink = NewOpenFolderLink(Paths.CurrentExe);
    grid.Controls.Add(_openExeFolderLink, 2, 0);

    grid.Controls.Add(NewBodyLabel("State dir:"), 0, 1);
    grid.Controls.Add(new Label { Text = Paths.StateDir, Font = monoFont, AutoSize = true, Margin = new Padding(0, 4, 8, 4) }, 1, 1);
    _openStateFolderLink = NewOpenFolderLink(Paths.StateDir);
    grid.Controls.Add(_openStateFolderLink, 2, 1);

    grid.Controls.Add(NewBodyLabel("Log file:"), 0, 2);
    grid.Controls.Add(new Label { Text = Paths.LogFile, Font = monoFont, AutoSize = true, Margin = new Padding(0, 4, 8, 4) }, 1, 2);
    var logActions = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true };
    _openLogFolderLink = NewOpenFolderLink(Paths.LogFile);
    _showLogBtn = new Button { Text = "Show Log", AutoSize = true, Margin = new Padding(8, 0, 0, 0), Padding = new Padding(8, 2, 8, 2) };
    _showLogBtn.Click += (s, e) => _tray.ShowLogFromControlPanel();
    logActions.Controls.Add(_openLogFolderLink);
    logActions.Controls.Add(_showLogBtn);
    grid.Controls.Add(logActions, 2, 2);

    box.Controls.Add(grid);
    return box;
}

private static LinkLabel NewOpenFolderLink(string path)
{
    var link = new LinkLabel
    {
        Text = "Open folder",
        AutoSize = true,
        Margin = new Padding(0, 4, 0, 4)
    };
    link.LinkClicked += (s, e) =>
    {
        try
        {
            string target = path;
            string args;
            if (File.Exists(target))
            {
                args = "/select,\"" + target + "\"";
            }
            else if (Directory.Exists(target))
            {
                args = "\"" + target + "\"";
            }
            else
            {
                // Path does not exist yet — open the parent dir if it exists.
                var parent = Path.GetDirectoryName(target);
                if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return;
                args = "\"" + parent + "\"";
            }
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex) { Logger.Error("Open folder failed: " + ex.Message); }
    };
    return link;
}

// -- About ----------------------------------------------------------
private Control BuildAboutSection()
{
    var box = NewGroupBox("About");
    var stack = new TableLayoutPanel
    {
        Dock = DockStyle.Fill,
        ColumnCount = 1,
        AutoSize = true,
        Padding = new Padding(8)
    };

    stack.Controls.Add(NewBodyLabel("DUVC API — Cellari kiosk camera control"));
    stack.Controls.Add(NewExternalLink("github.com/eriksp/duvc-api", "https://github.com/eriksp/duvc-api"));
    stack.Controls.Add(NewExternalLink("duvc-cli upstream: github.com/allanhanan/duvc-ctl", "https://github.com/allanhanan/duvc-ctl"));

    box.Controls.Add(stack);
    return box;
}

private static LinkLabel NewExternalLink(string text, string url)
{
    var link = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
    link.LinkClicked += (s, e) =>
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Error("Open link failed: " + ex.Message); }
    };
    return link;
}

// -- Footer ---------------------------------------------------------
private Control BuildFooter()
{
    var panel = new FlowLayoutPanel
    {
        FlowDirection = FlowDirection.RightToLeft,
        Dock = DockStyle.Top,
        AutoSize = true,
        Padding = new Padding(0, 8, 0, 0)
    };
    var close = new Button { Text = "Close", AutoSize = true, Padding = new Padding(12, 2, 12, 2) };
    close.Click += (s, e) => Close();
    var refresh = new Button { Text = "Refresh", AutoSize = true, Padding = new Padding(12, 2, 12, 2), Margin = new Padding(8, 0, 0, 0) };
    refresh.Click += (s, e) => RefreshAll();
    panel.Controls.Add(close);
    panel.Controls.Add(refresh);
    return panel;
}

// -- Refresh --------------------------------------------------------
private void RefreshAll()
{
    var svc = ServiceStatusHelper.GetStatus(Program.ServiceNameConst);
    var snap = _tray.GetHealthSnapshot();

    // Mode line
    if (svc.IsInstalled)
    {
        _modeLabel.Text = "Version " + Program.GetVersionLabel().TrimStart('v') + "  ·  Running with service watchdog";
    }
    else
    {
        _modeLabel.Text = "Version " + Program.GetVersionLabel().TrimStart('v') + "  ·  Running standalone";
    }

    // API
    if (snap.Status == null)
    {
        _apiBullet.SetColor(NaColor);
        _apiText.Text = "Pending first check…";
    }
    else
    {
        var s = snap.Status;
        var localTime = snap.CheckedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        if (!s.ApiReachable)
        {
            _apiBullet.SetColor(BadColor);
            _apiText.Text = "Down  (last check: " + localTime + ")";
        }
        else if (s.CameraFound)
        {
            _apiBullet.SetColor(OkColor);
            _apiText.Text = "OK  (last check: " + localTime + ", camera: " + (s.CameraName ?? "—") + ")";
        }
        else
        {
            _apiBullet.SetColor(WarnColor);
            _apiText.Text = "Reachable, camera missing  (last check: " + localTime + ")";
        }
    }

    // Watchdog
    if (!svc.IsInstalled)
    {
        _watchdogBullet.SetColor(NaColor);
        _watchdogText.Text = "N/A — standalone mode";
    }
    else if (svc.IsRunning)
    {
        _watchdogBullet.SetColor(OkColor);
        _watchdogText.Text = "OK";
    }
    else
    {
        _watchdogBullet.SetColor(BadColor);
        _watchdogText.Text = "Stopped";
    }

    // Service
    if (!svc.IsInstalled)
    {
        _serviceBullet.SetColor(NaColor);
        _serviceText.Text = "Not installed";
        _serviceStatusLabel.Text = "Status: Not installed";
        _installBtn.Enabled = true;
        _uninstallBtn.Enabled = false;
    }
    else if (svc.IsRunning)
    {
        _serviceBullet.SetColor(OkColor);
        _serviceText.Text = "Running";
        _serviceStatusLabel.Text = "Status: Running";
        _installBtn.Enabled = false;
        _uninstallBtn.Enabled = true;
    }
    else
    {
        _serviceBullet.SetColor(WarnColor);
        _serviceText.Text = "Installed, stopped";
        _serviceStatusLabel.Text = "Status: Installed, stopped";
        _installBtn.Enabled = false;
        _uninstallBtn.Enabled = true;
    }

    // Update
    var currentVer = Program.GetVersionLabel().TrimStart('v');
    _currentVerLabel.Text = "Current: " + currentVer;
    var avail = _updater != null ? _updater.AvailableUpdate : null;
    if (avail != null)
    {
        _latestVerLabel.Text = "    Latest: " + avail.Version;
        if (!_checking) _updateStatusLabel.Text = "    Status: Update available";
        _applyUpdateBtn.Enabled = true;
    }
    else
    {
        _latestVerLabel.Text = "    Latest: " + currentVer;
        if (!_checking) _updateStatusLabel.Text = "    Status: Up-to-date";
        _applyUpdateBtn.Enabled = false;
    }
}
```

- [ ] **Step 2: Add `TrayApp.OnUpdateClickedFromControlPanel` and `ShowLogFromControlPanel`**

Both `OnUpdateClicked()` and `ShowLog()` on `TrayApp` are private. Add two thin internal wrappers next to them so we do not change existing visibility:

```csharp
internal void OnUpdateClickedFromControlPanel()
{
    OnUpdateClicked();
}

internal void ShowLogFromControlPanel()
{
    ShowLog();
}
```

- [ ] **Step 3: Build to verify compilation**

Run: `powershell -ExecutionPolicy Bypass -File .\build.ps1`

Expected: exit 0.

- [ ] **Step 4: Commit**

```bash
git add src/DuvcApi/Program.cs
git commit -m "Implement ControlPanelForm update/paths/about sections and refresh logic"
```

---

## Task 7: Wire `ControlPanelForm` into the tray menu and double-click

**Files:**
- Modify: `src/DuvcApi/Program.cs`

- [ ] **Step 1: Add the field and helper method on `TrayApp`**

Add a field to the `TrayApp` field block:

```csharp
private ControlPanelForm _controlPanel;
```

Add a method to `TrayApp`:

```csharp
private void ShowControlPanel()
{
    try
    {
        if (_controlPanel == null || _controlPanel.IsDisposed)
        {
            _controlPanel = new ControlPanelForm(this, _updater);
            _controlPanel.FormClosed += (s, e) => _controlPanel = null;
            _controlPanel.Show();
        }
        else
        {
            if (_controlPanel.WindowState == FormWindowState.Minimized)
            {
                _controlPanel.WindowState = FormWindowState.Normal;
            }
            _controlPanel.BringToFront();
            _controlPanel.Activate();
        }
    }
    catch (Exception ex)
    {
        Logger.Error("Show control panel failed: " + ex.Message);
        MessageBox.Show("Failed to open Control Panel: " + ex.Message, Program.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
```

- [ ] **Step 2: Rewrite the menu construction**

Find the menu construction block in the `TrayApp` constructor (around line 1897-1927). Locate the lines that build `open`, `log`, and add them to `menu.Items`. Replace from:

```csharp
var open = new ToolStripMenuItem("Open Health Page");
open.Click += (sender, args) => OpenHealthPage();
var log = new ToolStripMenuItem("Show Log");
log.Click += (sender, args) => ShowLog();
```

to:

```csharp
var controlPanel = new ToolStripMenuItem("Show Control Panel");
controlPanel.Click += (sender, args) => ShowControlPanel();
```

And replace the corresponding `menu.Items.Add(open); menu.Items.Add(log);` lines with:

```csharp
menu.Items.Add(controlPanel);
```

Resulting menu (after both replacements) reads:

```csharp
menu.Items.Add(appTitle);
menu.Items.Add(new ToolStripSeparator());
menu.Items.Add(controlPanel);
menu.Items.Add(new ToolStripSeparator());
menu.Items.Add(_installServiceItem);
menu.Items.Add(_uninstallServiceItem);
menu.Items.Add(new ToolStripSeparator());
menu.Items.Add(_updateItem);
menu.Items.Add(exit);
```

The `OpenHealthPage()` method itself is no longer referenced from the menu. Leave the method body in place if `ShowLog()` or other code references it; otherwise delete it. Search the file: if there are zero remaining references to `OpenHealthPage()`, delete that method to avoid dead code.

- [ ] **Step 3: Repoint the tray double-click**

Find this line in `TrayApp` (around line 1935):

```csharp
_notifyIcon.DoubleClick += (sender, args) => ShowLog();
```

Change it to:

```csharp
_notifyIcon.DoubleClick += (sender, args) => ShowControlPanel();
```

- [ ] **Step 4: Build to verify compilation**

Run: `powershell -ExecutionPolicy Bypass -File .\build.ps1`

Expected: exit 0. If the compiler warns about unused `OpenHealthPage`, delete the method.

- [ ] **Step 5: Commit**

```bash
git add src/DuvcApi/Program.cs
git commit -m "Wire Control Panel to tray menu and double-click; remove Open Health Page + Show Log items"
```

---

## Task 8: Manual smoke verification

No automated tests exist. Run these checks on the dev machine (THEWOODS) with `dist\duvc-api.exe app` (standalone) and, if convenient, the service-mode subset.

**Files:** none modified.

- [ ] **Step 1: Build the latest binary**

Run: `powershell -ExecutionPolicy Bypass -File .\build.ps1`

Expected: `Built ...\dist\duvc-api.exe`.

- [ ] **Step 2: Visual icon check**

In Explorer at `dist\`, the icon for `duvc-api.exe` should be the Cellari logo (not the default `csc.exe` cog).

- [ ] **Step 3: Launch standalone and exercise the Control Panel**

Run: `.\dist\duvc-api.exe app`

Verify:

- Tray icon appears.
- Right-click → menu shows `Show Control Panel` (top), `Install Camera API as Service`, `Uninstall Camera API Service`, the Update item, `Exit`. **`Open Health Page` and `Show Log` are gone.**
- `Show Control Panel` opens the dialog. The Cellari logo is in the header. Title reads "DUVC API Control Panel". Mode line reads "Version 1.6.0 · Running standalone".
- Double-click the tray icon — the same Control Panel opens (or comes forward if already open). Opening twice does NOT create two windows.
- Health row "API" starts as "Pending first check…" then within 3 s goes to OK / Reachable, camera missing / Down (depending on camera state) with a `(last check: HH:mm:ss)` suffix. Bullet colour matches.
- Watchdog row shows "N/A — standalone mode" with a grey bullet.
- Service row shows "Not installed" with a grey bullet. `Install Service` button is enabled, `Uninstall Service` is disabled.
- Update section: `Current: 1.6.0`, `Latest: 1.6.0`, `Status: Up-to-date`, `Apply update` disabled. Click `Check for updates` — button briefly disables, status becomes `Checking…`, then returns to `Up-to-date` (or `Update available` if a newer release exists on GitHub).
- Installed files section: paths shown for executable / state dir / log file. `Open folder` link for executable opens Explorer with `dist\duvc-api.exe` selected. `Open folder` for state dir opens `C:\ProgramData\DuvcApi` (or its parent if it doesn't exist yet — running standalone may not have created it).
- `Show Log` button opens the log window (the same one `Show Log` used to open from the tray).
- About section: both `LinkLabel` links open the correct GitHub repos in the default browser.
- `Refresh` button forces an immediate refresh (last-check timestamp jumps to "now"). `Close` closes the dialog. Re-opening via tray works.

- [ ] **Step 4: Service-mode subset (if time allows on dev box; full matrix is Task 7 of the prior plan, run on a real kiosk)**

If you choose to install the service on the dev box:

- Click `Install Service` in the Control Panel. UAC prompts; app restarts elevated; service installs and starts.
- Re-open Control Panel after restart. Mode line now reads "Running with service watchdog". Service row green "Running". Watchdog row green "OK". Both `Install` and `Uninstall` button states flip (Install disabled, Uninstall enabled).
- Open `services.msc`, stop `DuvcApi`. Within 3 s the Control Panel should show Service "Installed, stopped" (yellow) and Watchdog "Stopped" (red).
- Re-start the service. Within 3 s status returns to green.
- Click `Uninstall Service`. UAC prompts; service is removed.

If anything in steps 3–4 fails, the implementation is not complete — fix and re-run, do not proceed to commit a "done" claim.

- [ ] **Step 5: Final integration matrix on real kiosk**

The full eight-check matrix from `docs/superpowers/plans/2026-05-15-service-watchdog-and-update.md` Task 7 still needs to run on a real kiosk after this feature lands. The Control Panel adds these additional items to that matrix (re-using kiosk install/update test runs):

- Open Control Panel from tray on kiosk → all sections render correctly under kiosk display scaling.
- Install Service / Uninstall Service buttons behave identically to the existing tray menu items.
- Apply update from Control Panel triggers the same service-IPC flow as the tray's Update menu item.

- [ ] **Step 6: No code commit for this task; record outcome in PR description**

Update the PR #1 description checklist with the additional Control Panel items. No source code commit.

---

## Notes for the implementer

- If `csc.exe` complains that `EmbeddedAssets` cannot find a resource at runtime, double-check that the short resource name in `build.ps1`'s `/resource:` flag (after the comma) matches the string passed to `GetManifestResourceStream`. They are case-sensitive.
- `Image.FromStream` keeps the underlying stream open; that is why `LoadPng` copies into a `MemoryStream` before disposing the resource stream.
- `_refreshTimer` runs on the UI thread (it's a `System.Windows.Forms.Timer`), so `RefreshAll()` does not need `Invoke`. Only the async update-check trampoline (`Task.Run`) needs `BeginInvoke`.
- Do not change behaviour of the existing tray Update item — `ControlPanelForm` calls into the same `OnUpdateClicked()` path via the thin wrapper.
- Keep file size in mind: `Program.cs` is already large. If this push past 3000 lines materially harms readability, a follow-up refactor into partial classes is reasonable — but that is outside the scope of this plan.
