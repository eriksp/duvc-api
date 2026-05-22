<#
.SYNOPSIS
  Configure Windows 10 kiosk for auto-connect to an open SSID (default: Gaestenet),
  disable Microsoft Edge Visual Search & sidebar/Copilot/Discover, optionally disable
  Windows automatic updates, install Tailscale + the Camera Control API, and revert
  execution policy.

.NOTES
  Run in elevated PowerShell (Administrator).
  Set-ExecutionPolicy Unrestricted -Scope Process -Force

  All interactive prompts are collected up front; long-running tasks (downloads,
  Tailscale login, Camera Control API install, debloater) run at the end so the
  operator can answer the questions and then walk away.
#>

[CmdletBinding()]
param()

# --- Execution Policy Preflight ---
try {
    $processPolicy = Get-ExecutionPolicy -Scope Process -ErrorAction SilentlyContinue
} catch {
    $processPolicy = $null
}
if ($processPolicy -ne 'Unrestricted') {
    try {
        Set-ExecutionPolicy Unrestricted -Scope Process -Force -ErrorAction Stop
        Write-Host "Process ExecutionPolicy set to Unrestricted."
    } catch {
        $cmd = 'Set-ExecutionPolicy Unrestricted -Scope Process -Force'
        Write-Warning "Could not set ExecutionPolicy for this process automatically."
        Write-Host "Copying the required command to clipboard so you can paste it in PowerShell..."
        try {
            Set-Clipboard -Value $cmd -ErrorAction Stop
        } catch {
            try { $cmd | clip } catch {}
        }
        Write-Host $cmd
    }
}

# --- Helpers ---
function Assert-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p  = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Error "This script must be run as Administrator."
        exit 1
    }
}

function Read-HostWithDefault {
    param(
        [string]$Prompt,
        [string]$Default
    )
    # Pre-fill the input line with $Default so the operator can just press Enter
    # (or edit the value). SendKeys only fires for a safe character set; if it is
    # unavailable (non-interactive host) we still fall back to default-on-empty.
    if (-not [string]::IsNullOrEmpty($Default) -and $Default -match '^[A-Za-z0-9._-]+$') {
        try {
            Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
            [System.Windows.Forms.SendKeys]::SendWait($Default)
        } catch {
            # SendKeys unavailable - fall back to plain prompt behaviour
        }
    }
    $value = Read-Host $Prompt
    if ([string]::IsNullOrWhiteSpace($value)) { return $Default }
    return $value.Trim()
}

function New-WlanProfileXml {
    param([string]$Name, [string]$Path)
    $xml = @"
<?xml version="1.0"?>
<WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
  <name>$Name</name>
  <SSIDConfig>
    <SSID>
      <name>$Name</name>
    </SSID>
  </SSIDConfig>
  <connectionType>ESS</connectionType>
  <connectionMode>auto</connectionMode>
  <MSM>
    <security>
      <authEncryption>
        <authentication>open</authentication>
        <encryption>none</encryption>
        <useOneX>false</useOneX>
      </authEncryption>
    </security>
  </MSM>
</WLANProfile>
"@
    Set-Content -LiteralPath $Path -Value $xml -Encoding UTF8 -Force
}

function Get-WifiInterfaceNames {
    $out = netsh wlan show interfaces
    $names = @()
    $m = $out | Select-String -Pattern '^\s*Name\s*:\s*(.+)$'
    if ($m) { $names += ($m.Matches | ForEach-Object { $_.Groups[1].Value.Trim() }) }
    $m = $out | Select-String -Pattern '^\s*Navn\s*:\s*(.+)$'   # Danish fallback
    if ($m) { $names += ($m.Matches | ForEach-Object { $_.Groups[1].Value.Trim() }) }
    return $names
}

function Get-AllProfiles {
    $out = netsh wlan show profiles 2>$null
    if (-not $out) { return @() }
    ($out | Where-Object { $_ -match ':\s*.+$' } |
        ForEach-Object { ($_ -split ':', 2)[1].Trim() } |
        Where-Object { $_ -ne "" } |
        Select-Object -Unique)
}

function Profile-Exists { param([string]$Name) (Get-AllProfiles) -contains $Name }

function Set-RegDword {
    param([string]$Path, [string]$Name, [int]$Value)
    if (-not (Test-Path $Path)) { New-Item -Path $Path -Force | Out-Null }
    New-ItemProperty -Path $Path -Name $Name -Value $Value -PropertyType DWord -Force | Out-Null
}

function Start-ProcessWithTimeout {
    param(
        [string]$FilePath,
        [string]$ArgumentList = "",
        [int]$TimeoutSeconds = 180,
        [string]$ActionLabel = "process"
    )
    $proc = Start-Process -FilePath $FilePath -ArgumentList $ArgumentList -PassThru -WindowStyle Normal
    $completed = $proc | Wait-Process -Timeout $TimeoutSeconds -ErrorAction SilentlyContinue
    if (-not $completed) {
        try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch {}
        throw "Timed out after $TimeoutSeconds seconds while waiting for $ActionLabel."
    }
}

# --- MAIN ---
Assert-Admin

# =====================================================================
# STEP 1: Collect all settings up front. No long-running work happens
# here, so the operator can answer everything and then walk away while
# the downloads/installs run unattended (STEP 3).
# =====================================================================
Write-Host "`n=== Kiosk setup: please answer the following ===`n"

# Wi-Fi SSID
$Ssid = Read-Host "Enter SSID name (default: Gaestenet)"
if ([string]::IsNullOrWhiteSpace($Ssid)) { $Ssid = "Gaestenet" }
Write-Host "Using SSID: $Ssid"

$prioInput = Read-Host "Set this SSID as top priority? (Y/n, default: Y)"
$SetTopPriority = if ([string]::IsNullOrWhiteSpace($prioInput)) { $true } else { $prioInput.ToLower() -ne "n" }
Write-Host "Top priority setting: $SetTopPriority"

# Hostname (used by Tailscale friendly name). The current computer name is
# pre-filled, so pressing Enter keeps it (e.g. cellari-tablet-10).
$defaultHostname = $env:COMPUTERNAME
$Hostname = Read-HostWithDefault -Prompt "Enter OS Hostname (used by Tailscale)" -Default $defaultHostname
Write-Host "Using hostname: $Hostname"

# Remote Desktop access
$rdpInput = Read-Host "Enable Remote Desktop access and open firewall rules? (Y/n)"
$EnableRdp = if ([string]::IsNullOrWhiteSpace($rdpInput)) { $true } else { $rdpInput.ToLower() -ne "n" }

# Network hardening
$hardenInput = Read-Host "Harden device: block LAN discovery and restrict RDP to Tailscale only? (Y/n)"
$HardenDevice = if ([string]::IsNullOrWhiteSpace($hardenInput)) { $true } else { $hardenInput.ToLower() -ne "n" }

# Windows automatic updates (default Y: disable, so OS updates do not disrupt the kiosk)
$wuInput = Read-Host "Disable Windows automatic updates (recommended for kiosk stability)? (Y/n)"
$DisableWindowsUpdate = if ([string]::IsNullOrWhiteSpace($wuInput)) { $true } else { $wuInput.ToLower() -ne "n" }

# Tailscale
$tsInput = Read-Host "Install and start Tailscale now for secure remote access? (Y/n)"
$InstallTailscale = if ([string]::IsNullOrWhiteSpace($tsInput)) { $true } else { $tsInput.ToLower() -ne "n" }

$unattInput = Read-Host "  -> Enable Tailscale unattended mode so it stays connected pre-login? (Y/n)"
$EnableTsUnatt = if ([string]::IsNullOrWhiteSpace($unattInput)) { $true } else { $unattInput.ToLower() -ne 'n' }

# Camera Control API (duvc-api)
$duvcInput = Read-Host "Install Camera Control API service (duvc-api) now? (Y/n)"
$InstallDuvc = if ([string]::IsNullOrWhiteSpace($duvcInput)) { $true } else { $duvcInput.ToLower() -ne "n" }

# Windows 10 Debloater (long-running)
$debloatInput = Read-Host "Download full Windows10Debloater repo and run SysPrep debloater now? (Y/n)"
$RunDebloater = if ([string]::IsNullOrWhiteSpace($debloatInput)) { $true } else { $debloatInput.ToLower() -ne "n" }

Write-Host "`n=== All settings collected. Applying configuration... ===`n"

# =====================================================================
# STEP 2: Quick local configuration (fast, no network or interaction).
# =====================================================================

# --- Hostname ---
if ($Hostname -ne $env:COMPUTERNAME) {
    try {
        Rename-Computer -NewName $Hostname -Force -ErrorAction Stop
        Write-Host "Hostname set to '$Hostname'. A restart is required for it to fully apply."
    } catch {
        Write-Warning "Failed to set hostname to '$Hostname': $($_.Exception.Message)"
        Write-Warning "Ensure the name meets Windows constraints (letters, numbers, hyphens; may require <= 15 chars)."
    }
} else {
    Write-Host "Hostname already set to '$Hostname'."
}

# --- Remote Desktop access ---
if ($EnableRdp) {
    try {
        Write-Host "Enabling Remote Desktop and firewall rules..."
        Set-ItemProperty -Path 'HKLM:\System\CurrentControlSet\Control\Terminal Server' -Name 'fDenyTSConnections' -Value 0 -Type DWord
        try { Enable-NetFirewallRule -DisplayGroup "Remote Desktop" -ErrorAction Stop | Out-Null } catch { netsh advfirewall firewall set rule group="remote desktop" new enable=Yes | Out-Null }
        try { Start-Service -Name TermService -ErrorAction SilentlyContinue } catch {}
        Write-Host "Remote Desktop enabled."
    } catch {
        Write-Warning "Failed to enable Remote Desktop: $($_.Exception.Message)"
    }
}

# --- Network hardening: restrict LAN access, allow Tailscale + RDP via Tailscale only ---
if ($HardenDevice) {
    try {
        Write-Host "Applying firewall hardening..."
        try { Set-NetFirewallProfile -Profile Domain,Private,Public -DefaultInboundAction Block -DefaultOutboundAction Allow | Out-Null } catch {}

        # Disable network discovery and file/printer sharing inbound rules
        try { Disable-NetFirewallRule -DisplayGroup "Network Discovery" -ErrorAction Stop | Out-Null } catch { netsh advfirewall firewall set rule group="network discovery" new enable=No | Out-Null }
        try { Disable-NetFirewallRule -DisplayGroup "File and Printer Sharing" -ErrorAction Stop | Out-Null } catch { netsh advfirewall firewall set rule group="file and printer sharing" new enable=No | Out-Null }

        # Restrict RDP to Tailscale only
        try { Disable-NetFirewallRule -DisplayGroup "Remote Desktop" -ErrorAction Stop | Out-Null } catch { netsh advfirewall firewall set rule group="remote desktop" new enable=No | Out-Null }
        if ($EnableRdp) {
            $rdpRuleName = "RDP (Tailscale only)"
            try { Get-NetFirewallRule -DisplayName $rdpRuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue } catch {}

            $tsIfAliases = @()
            try {
                $tsIfAliases = (Get-NetAdapter -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name -like 'Tailscale*' -or $_.InterfaceDescription -like '*Tailscale*' } |
                    Select-Object -ExpandProperty Name)
            } catch {}

            $ruleParams = @{
                DisplayName  = $rdpRuleName
                Direction    = 'Inbound'
                Action       = 'Allow'
                Protocol     = 'TCP'
                LocalPort    = 3389
                RemoteAddress = '100.64.0.0/10'
                Profile      = 'Any'
            }
            if ($tsIfAliases -and $tsIfAliases.Count -gt 0) { $ruleParams['InterfaceAlias'] = $tsIfAliases }
            try {
                New-NetFirewallRule @ruleParams | Out-Null
            } catch {
                netsh advfirewall firewall delete rule name="$rdpRuleName" | Out-Null
                netsh advfirewall firewall add rule name="$rdpRuleName" dir=in action=allow protocol=TCP localport=3389 remoteip=100.64.0.0/10 | Out-Null
            }
        }

        # Cleanup legacy rules from previous runs
        $legacyRules = @(
            "Block LAN (RFC1918) outbound",
            "Allow DNS to resolvers"
        )
        foreach ($legacy in $legacyRules) {
            try { Get-NetFirewallRule -DisplayName $legacy -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue } catch {}
        }

        # Allow DNS to any resolver
        $dnsRuleName = "Allow DNS (any resolver)"
        try { Get-NetFirewallRule -DisplayName $dnsRuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue } catch {}
        try {
            New-NetFirewallRule -DisplayName $dnsRuleName -Direction Outbound -Action Allow -Protocol UDP -RemotePort 53 -Profile Any | Out-Null
            New-NetFirewallRule -DisplayName "$dnsRuleName (TCP)" -Direction Outbound -Action Allow -Protocol TCP -RemotePort 53 -Profile Any | Out-Null
        } catch {
            netsh advfirewall firewall delete rule name="$dnsRuleName" | Out-Null
            netsh advfirewall firewall add rule name="$dnsRuleName" dir=out action=allow protocol=UDP remoteport=53 | Out-Null
            netsh advfirewall firewall add rule name="$dnsRuleName (TCP)" dir=out action=allow protocol=TCP remoteport=53 | Out-Null
        }

        # Allow DHCP to acquire/renew leases
        $dhcpRuleName = "Allow DHCP client"
        try { Get-NetFirewallRule -DisplayName $dhcpRuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue } catch {}
        try {
            New-NetFirewallRule -DisplayName $dhcpRuleName -Direction Outbound -Action Allow -Protocol UDP -LocalPort 68 -RemotePort 67 -RemoteAddress "255.255.255.255" -Profile Any | Out-Null
        } catch {
            netsh advfirewall firewall delete rule name="$dhcpRuleName" | Out-Null
            netsh advfirewall firewall add rule name="$dhcpRuleName" dir=out action=allow protocol=UDP localport=68 remoteport=67 remoteip=255.255.255.255 | Out-Null
        }

        # Prefer Public profile for active connections to reduce exposure
        try { Get-NetConnectionProfile | Where-Object { $_.NetworkCategory -ne 'Public' } | Set-NetConnectionProfile -NetworkCategory Public } catch {}

        Write-Host "Firewall hardening applied. LAN discovery disabled; RDP limited to Tailscale."
    } catch {
        Write-Warning "Failed to apply firewall hardening: $($_.Exception.Message)"
    }
}

# --- Disable Windows automatic updates ---
# Keeps the kiosk on a known-good OS build so a surprise feature/quality update
# cannot reboot the device or break the camera stack mid-shift. Combines the
# WindowsUpdate AU policy (durable) with disabling the wuauserv service.
if ($DisableWindowsUpdate) {
    try {
        Write-Host "Disabling Windows automatic updates..."
        $auPath = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU'
        if (-not (Test-Path $auPath)) { New-Item -Path $auPath -Force | Out-Null }
        New-ItemProperty -Path $auPath -Name 'NoAutoUpdate' -Value 1 -PropertyType DWord -Force | Out-Null
        New-ItemProperty -Path $auPath -Name 'AUOptions'   -Value 1 -PropertyType DWord -Force | Out-Null
        try { Stop-Service -Name wuauserv -Force -ErrorAction SilentlyContinue } catch {}
        try { Set-Service  -Name wuauserv -StartupType Disabled -ErrorAction SilentlyContinue } catch {}
        Write-Host "Windows automatic updates disabled (AU policy + wuauserv service)."
    } catch {
        Write-Warning "Failed to disable Windows Update: $($_.Exception.Message)"
    }
}

# --- WLAN profile: create, import, verify, auto-connect, priority ---
$xmlPath = Join-Path $env:TEMP "$($Ssid).xml"
Write-Host "Creating WLAN profile XML at: $xmlPath"
New-WlanProfileXml -Name $Ssid -Path $xmlPath

Write-Host "Importing profile for all users (generic add)..."
$addOut = netsh wlan add profile filename="$xmlPath" user=all | Out-String
if ($addOut -notmatch 'added|exists|konfigureret|tilføjet') {
    Write-Warning "Add output (for reference):`n$addOut"
}

# If not visible yet, add per interface
if (-not (Profile-Exists -Name $Ssid)) {
    $ifs = Get-WifiInterfaceNames
    foreach ($if in $ifs) {
        Write-Host "Also adding profile on interface: $if"
        netsh wlan add profile filename="$xmlPath" user=all interface="$if" | Out-Null
    }
}

# Retry verification
$verified = $false
foreach ($i in 1..5) {
    if (Profile-Exists -Name $Ssid) { $verified = $true; break }
    Start-Sleep -Milliseconds 400
}
if (-not $verified) {
    Write-Error "Verification failed: profile '$Ssid' not found after import."
    netsh wlan show profiles
    exit 3
}

# Auto-connect + priority
Write-Host "Profile found. Enabling auto-connect..."
$setOutText = (netsh wlan set profileparameter name="$Ssid" connectionmode=auto | Out-String)
if ($setOutText -match 'successfully|opdateret|updated') { Write-Host "Auto-connect enabled." }
else { Write-Warning "Could not confirm connectionmode=auto. Output:`n$setOutText" }

if ($SetTopPriority) {
    $ifs = Get-WifiInterfaceNames
    foreach ($if in $ifs) {
        Write-Host "Setting priority=1 on interface: $if"
        $prioOutText = (netsh wlan set profileorder name="$Ssid" interface="$if" priority=1 | Out-String)
        if ($prioOutText -match 'successfully|not changed|opdateret|ikke ændret|updated') {
            Write-Host "Priority set (or already #1) on '$if'."
        } else {
            Write-Warning "Could not set priority on '$if'. Output:`n$prioOutText"
        }
    }
}

# Delete XML
try {
    Remove-Item -LiteralPath $xmlPath -Force -ErrorAction Stop
    Write-Host "Temporary XML deleted."
} catch {
    Write-Warning "Failed to delete temporary XML: $($_.Exception.Message)"
}

# --- Harden Edge for kiosk: disable Visual Search + sidebar/Discover/Copilot ---
Write-Host "`nDisabling Microsoft Edge Visual Search and Sidebar features..."
$HKLM_EdgePolicies = 'HKLM:\SOFTWARE\Policies\Microsoft\Edge'
$HKCU_EdgePolicies = 'HKCU:\SOFTWARE\Policies\Microsoft\Edge'

# Visual Search (image hover/context menu/sidebar search)
Set-RegDword -Path $HKLM_EdgePolicies -Name 'VisualSearchEnabled' -Value 0
Set-RegDword -Path $HKCU_EdgePolicies -Name 'VisualSearchEnabled' -Value 0

# Disable the Edge Sidebar entirely (hides Copilot/Discover UI in sidebar)
Set-RegDword -Path $HKLM_EdgePolicies -Name 'HubsSidebarEnabled' -Value 0
Set-RegDword -Path $HKLM_EdgePolicies -Name 'StandaloneHubsSidebarEnabled' -Value 0

# (Optional hardening) Disable "Search the web for image" in context menu
Set-RegDword -Path $HKLM_EdgePolicies -Name 'SearchForImageEnabled' -Value 0

# Old Discover policy is deprecated but set defensively (no harm if ignored)
Set-RegDword -Path $HKLM_EdgePolicies -Name 'EdgeDiscoverEnabled' -Value 0

# Apply by closing Edge if open
Get-Process -Name msedge -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Write-Host "Edge policies set. (Restart Edge for effect.)"

# --- Power: Stay Awake (No Sleep, Display Off only) ---
try {
    $displayMinutes = 15
    Write-Host "Configuring power: never sleep (AC/DC), display off after $displayMinutes min, disable hibernate..."
    powercfg /change standby-timeout-ac 0 | Out-Null
    powercfg /change standby-timeout-dc 0 | Out-Null
    powercfg /change monitor-timeout-ac $displayMinutes | Out-Null
    powercfg /change monitor-timeout-dc $displayMinutes | Out-Null
    powercfg -hibernate off | Out-Null
    Write-Host "Power settings updated. The PC will not sleep; only the display will turn off."
} catch {
    Write-Warning "Failed to update power settings: $($_.Exception.Message)"
}

# =====================================================================
# STEP 3: Long-running tasks (downloads, Tailscale login, API install,
# debloater). These run last so the operator is not interrupted while
# the quick configuration above is applied.
# =====================================================================
Write-Host "`n=== Running long tasks (downloads / installs)... ===`n"

# --- Download offline page for kiosk ---
try {
    $kioskDir = 'C:\Kiosk'
    if (-not (Test-Path -LiteralPath $kioskDir)) {
        New-Item -ItemType Directory -Path $kioskDir -Force | Out-Null
    }
    $offlineUrl = 'https://colpo.cellari.io/assets/offline.html'
    $offlinePath = Join-Path $kioskDir 'offline.html'
    Write-Host "Downloading offline page to $offlinePath ..."
    Invoke-WebRequest -Uri $offlineUrl -OutFile $offlinePath -UseBasicParsing -ErrorAction Stop
} catch {
    Write-Warning "Failed to download offline page: $($_.Exception.Message)"
}

# --- Tailscale (install, login, unattended) ---
if ($InstallTailscale) {
    try {
        # Detect existing install
        $tailscaleExe = ''
        $candidatePaths = @(
            (Join-Path $env:ProgramFiles 'Tailscale\tailscale.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Tailscale\tailscale.exe')
        )
        foreach ($p in $candidatePaths) { if (Test-Path $p) { $tailscaleExe = $p; break } }

        if (-not $tailscaleExe) {
            # Download EXE installer from stable channel (generic latest), fallback to latest versioned EXE
            $baseUrl = 'https://pkgs.tailscale.com/stable'
            $exeUrl = "$baseUrl/tailscale-setup-latest.exe"
            $tsExe  = Join-Path $env:TEMP 'tailscale-setup-latest.exe'
            Write-Host "Downloading Tailscale installer..."
            try {
                Invoke-WebRequest -Uri $exeUrl -OutFile $tsExe -UseBasicParsing -ErrorAction Stop
            } catch {
                Write-Warning "Latest EXE not found at $exeUrl. Attempting to detect latest version from stable index..."
                $indexHtml = Invoke-WebRequest -Uri ($baseUrl + '/') -UseBasicParsing -ErrorAction Stop
                $regex = "tailscale-setup-[0-9\\.]+\.exe"
                $match = [regex]::Matches($indexHtml.Content, $regex) | Select-Object -First 1
                if (-not $match) { throw "Unable to locate a Tailscale EXE on $baseUrl" }
                $fileName = $match.Value
                $exeUrl = "$baseUrl/$fileName"
                $tsExe  = Join-Path $env:TEMP $fileName
                Write-Host "Downloading $fileName ..."
                Invoke-WebRequest -Uri $exeUrl -OutFile $tsExe -UseBasicParsing -ErrorAction Stop
            }

            Write-Host "Installing Tailscale..."
            $installed = $false
            $installArgsList = @('/S', '/quiet', '/install /quiet', '')
            foreach ($args in $installArgsList) {
                try {
                    if ($args -eq '') {
                        Start-Process -FilePath $tsExe -Wait
                    } else {
                        Start-Process -FilePath $tsExe -ArgumentList $args -Wait
                    }
                    foreach ($p in $candidatePaths) { if (Test-Path $p) { $tailscaleExe = $p; break } }
                    if ($tailscaleExe) { $installed = $true; break }
                } catch { }
            }
            if (-not $installed) { throw "Tailscale installation failed or tailscale.exe not found." }
        } else {
            Write-Host "Tailscale already installed; skipping download."
        }

        # Ensure service and login
        $svcName = 'Tailscale'
        try { Set-Service -Name $svcName -StartupType Automatic } catch {}
        try { Start-Service -Name $svcName -ErrorAction SilentlyContinue } catch {}

        Write-Host "Launching Tailscale login flow... Complete the login in your browser."
        $tsArgs = "up --hostname=$Hostname"
        Start-Process -FilePath $tailscaleExe -ArgumentList $tsArgs -WindowStyle Normal -Wait

        if ($EnableTsUnatt) {
            try { if (-not (Test-Path 'HKLM:\SOFTWARE\Tailscale IPN')) { New-Item -Path 'HKLM:\SOFTWARE\Tailscale IPN' -Force | Out-Null } } catch {}
            try { New-ItemProperty -Path 'HKLM:\SOFTWARE\Tailscale IPN' -Name 'UnattendedMode' -Value 1 -PropertyType DWord -Force | Out-Null } catch {}
            try { Restart-Service -Name $svcName -Force -ErrorAction SilentlyContinue } catch {}
            Write-Host "Tailscale unattended mode enabled."
        }
    } catch {
        Write-Warning "Failed to install/start Tailscale: $($_.Exception.Message)"
    }
}

# --- Camera Control API (duvc-api) ---
if ($InstallDuvc) {
    try {
        $duvcExeUrl = 'https://github.com/eriksp/duvc-api/releases/download/v1.7.0/duvc-api.exe'
        $duvcShaUrl = 'https://github.com/eriksp/duvc-api/releases/download/v1.7.0/duvc-api.exe.sha256'
        # Keep the API alongside the other kiosk files in C:\Kiosk. duvc-cli.exe
        # is embedded in duvc-api.exe and the API extracts it next to itself.
        $duvcDir = 'C:\Kiosk'
        $duvcExePath = Join-Path $duvcDir 'duvc-api.exe'
        $duvcServiceName = 'DuvcApi'

        $service = Get-Service -Name $duvcServiceName -ErrorAction SilentlyContinue
        if (-not $service -or -not (Test-Path $duvcExePath)) {
            $tmpExe = Join-Path $env:TEMP 'duvc-api.exe'
            $tmpSha = Join-Path $env:TEMP 'duvc-api.exe.sha256'

            Write-Host "Downloading Camera Control API (duvc-api)..."
            Invoke-WebRequest -Uri $duvcExeUrl -OutFile $tmpExe -UseBasicParsing -ErrorAction Stop
            Invoke-WebRequest -Uri $duvcShaUrl -OutFile $tmpSha -UseBasicParsing -ErrorAction Stop

            $shaLine = (Get-Content -LiteralPath $tmpSha -ErrorAction Stop | Select-Object -First 1)
            $expectedHash = ($shaLine -split '\s+')[0].Trim().ToUpperInvariant()
            $actualHash = (Get-FileHash -Algorithm SHA256 -Path $tmpExe).Hash.ToUpperInvariant()
            if ($expectedHash -ne $actualHash) {
                throw "SHA256 mismatch for duvc-api.exe (expected $expectedHash, got $actualHash)."
            }

            if (-not (Test-Path -LiteralPath $duvcDir)) {
                New-Item -ItemType Directory -Path $duvcDir -Force | Out-Null
            }
            Copy-Item -LiteralPath $tmpExe -Destination $duvcExePath -Force

            Write-Host "Installing Camera Control API service..."
            Start-ProcessWithTimeout -FilePath $duvcExePath -ArgumentList 'install' -TimeoutSeconds 120 -ActionLabel 'duvc-api install'
        } else {
            Write-Host "Camera Control API already installed; ensuring service is running..."
        }

        # `duvc-api.exe install` now creates the service with start=auto and
        # starts it itself, so the explicit Set-Service/Start-Service calls
        # that used to live here are redundant.

        try {
            $svcState = (Get-Service -Name $duvcServiceName -ErrorAction SilentlyContinue).Status
            if ($svcState -ne 'Running') {
                Write-Host "Service not running yet; waiting briefly..."
                Start-Sleep -Seconds 3
                $svcState = (Get-Service -Name $duvcServiceName -ErrorAction SilentlyContinue).Status
            }
            Write-Host "Camera Control API service status: $svcState"
            $healthUrl = 'http://127.0.0.1:3790/health'
            Write-Host "Checking duvc-api health at $healthUrl ..."
            $health = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
            Write-Host "duvc-api health response: $($health.Content)"
        } catch {
            Write-Warning "duvc-api health check failed: $($_.Exception.Message)"
        }
    } catch {
        Write-Warning "Failed to install/start Camera Control API: $($_.Exception.Message)"
    } finally {
        try { if ($tmpExe -and (Test-Path $tmpExe)) { Remove-Item -LiteralPath $tmpExe -Force } } catch {}
        try { if ($tmpSha -and (Test-Path $tmpSha)) { Remove-Item -LiteralPath $tmpSha -Force } } catch {}
    }
}

# --- Optional: Windows 10 Debloater (Full repo, at end) ---
if ($RunDebloater) {
    try {
        $zipUrl   = 'https://github.com/Sycnex/Windows10Debloater/archive/refs/heads/master.zip'
        $zipPath  = Join-Path $env:TEMP 'Windows10Debloater.zip'
        $extract  = Join-Path $env:TEMP 'Windows10Debloater-master'
        Write-Host "Downloading Windows10Debloater repository (ZIP)..."
        Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -UseBasicParsing -ErrorAction Stop
        if (Test-Path $extract) { Remove-Item -Recurse -Force -LiteralPath $extract }
        Write-Host "Extracting..."
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $env:TEMP)
        $debloaterScript = Join-Path $extract 'Windows10SysPrepDebloater.ps1'
        if (-not (Test-Path $debloaterScript)) { throw "Debloater script not found after extraction." }
        Write-Host "Running debloater with -Sysprep -Debloat -Privacy (this can take a long time)..."
        Start-Process -FilePath powershell.exe -ArgumentList "-ExecutionPolicy Bypass -NoProfile -File `"$debloaterScript`" -Sysprep -Debloat -Privacy" -WorkingDirectory $extract -Wait
        Write-Host "Debloater completed."
    } catch {
        Write-Warning "Debloater failed: $($_.Exception.Message)"
    } finally {
        try { if (Test-Path $zipPath) { Remove-Item -LiteralPath $zipPath -Force } } catch {}
    }
}

# --- Revert Execution Policy ---
Write-Host "`nReverting PowerShell ExecutionPolicy to Restricted..."
try {
    Set-ExecutionPolicy Restricted -Scope LocalMachine -Force
    Write-Host "ExecutionPolicy reverted to Restricted."
} catch {
    Write-Warning "Could not revert ExecutionPolicy: $($_.Exception.Message)"
}
Write-Host "`nDone. '$Ssid' will auto-connect when detected. Edge Visual Search & sidebar/Copilot/Discover disabled. Sleep disabled (display off after 15 min). RDP enabled if you chose it. ExecutionPolicy reverted."
exit 0
