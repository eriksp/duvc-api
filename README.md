# duvc-api

Cellari Camera Control API is a local API + tray indicator for controlling a USB camera using the bundled `duvc-cli.exe`.
It is a wrapper around the [`duvc-ctl` CLI tool](https://github.com/allanhanan/duvc-ctl), which provides DirectShow UVC
camera control on Windows.
Designed for Windows 10/11 kiosks: single EXE, no runtime dependencies, service install
via PowerShell, and a small tray icon that shows whether the camera named `USB Camera`
is connected.

## Build

This project builds a single `duvc-api.exe` that embeds `bin/duvc-cli.exe`.

```powershell
.\build.ps1
```

Output: `dist\duvc-api.exe`

## Install (service)

Run these commands as Administrator:

```powershell
.\dist\duvc-api.exe install
```

Install will:

- Create the Windows service for the API
- Create a Scheduled Task to start the tray icon at user login
- Attempt to start the tray immediately (current user)

Uninstall:

```powershell
.\dist\duvc-api.exe uninstall
```

## Kiosk automation (PowerShell)

When installing from another script (for example, a kiosk provisioning script), run
the service install step after creating the kiosk user. The install must run elevated.

Example (run as Administrator):

```powershell
# Create kiosk user (example; adjust password policy as needed)
$userName = "kiosk"
$password = ConvertTo-SecureString "ChangeMeNow!" -AsPlainText -Force
if (-not (Get-LocalUser -Name $userName -ErrorAction SilentlyContinue)) {
  New-LocalUser -Name $userName -Password $password -PasswordNeverExpires
  Add-LocalGroupMember -Group "Users" -Member $userName
}

# Install the service + tray startup task
& "C:\Path\To\dist\duvc-api.exe" install
```

The install command creates a Scheduled Task that starts the tray icon on user
logon, which works for kiosk mode once the kiosk user signs in.

Run service in console mode (useful for debugging):

```powershell
.\dist\duvc-api.exe run
```

Note: the app is built as a Windows GUI executable, so `run` writes to the log
file instead of keeping a visible console window open.

## Tray icon

Start the tray indicator only (per-user, no API server):

```powershell
.\dist\duvc-api.exe tray
```

The icon turns green when `USB Camera` is connected and the local API is reachable,
and red if the API is down or the camera is missing.

For kiosk mode, the tray icon starts at user login via a Scheduled Task created
by `install` (runs as the logged-on user).

Launch the full app (tray + API in one process):

```powershell
.\dist\duvc-api.exe app
```

Double-clicking `duvc-api.exe` also starts the tray + API.

## API

Base URL: `http://127.0.0.1:3790`

Health:

```
GET /health
```

Example response:

```json
{
  "ok": true,
  "status": "ready",
  "cameraFound": true,
  "cameraName": "USB Camera",
  "cameraIndex": 0,
  "appVersion": "v1.0.0",
  "devices": [
    { "index": 0, "name": "USB Camera" }
  ]
}
```

List devices:

```
GET /api/cameras
```

Set a value (auto-focus support: returns `"OK"` from `duvc-cli`):

```
POST /api/usb-camera/set
```

Body:

```json
{
  "domain": "cam",
  "property": "Focus",
  "value": "200",
  "cameraName": "USB Camera"
}
```

Example response:

```json
{
  "ok": true,
  "output": "OK",
  "error": "",
  "exitCode": 0
}
```

### Focus control (website usage)

To change focus on a camera named `"USB Camera"` from a website, call:

```
POST /api/usb-camera/set
```

Body:

```json
{
  "domain": "cam",
  "property": "Focus",
  "value": "200",
  "cameraName": "USB Camera"
}
```

Auto-focus mode:

```json
{
  "domain": "cam",
  "property": "Focus",
  "mode": "auto",
  "cameraName": "USB Camera"
}
```

PowerShell example:

```powershell
Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:3790/api/usb-camera/set" -ContentType "application/json" -Body '{"domain":"cam","property":"Focus","value":"200","cameraName":"USB Camera"}'
```

Focus (PowerShell examples):

```bash
curl.exe -X POST "http://127.0.0.1:3790/api/usb-camera/set" -H "Content-Type: application/json" -d "{\"domain\":\"cam\",\"property\":\"Focus\",\"value\":\"200\"}"
```

```powershell
Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:3790/api/usb-camera/set" -ContentType "application/json" -Body '{"domain":"cam","property":"Focus","value":"200"}'
```

Auto-focus mode:

```powershell
curl.exe -X POST "http://127.0.0.1:3790/api/usb-camera/set" -H "Content-Type: application/json" -d "{\"domain\":\"cam\",\"property\":\"Focus\",\"mode\":\"auto\"}"
```

```powershell
Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:3790/api/usb-camera/set" -ContentType "application/json" -Body '{"domain":"cam","property":"Focus","mode":"auto"}'
```

Get values:

```
POST /api/usb-camera/get
```

Body:

```json
{
  "domain": "cam",
  "properties": ["Focus", "Exposure"],
  "cameraName": "USB Camera"
}
```

Reset:

```
POST /api/usb-camera/reset
```

Body:

```json
{
  "domain": "cam",
  "property": "Focus",
  "cameraName": "USB Camera"
}
```

Capabilities:

```
GET /api/usb-camera/capabilities
```

Query param (optional):

```
GET /api/usb-camera/capabilities?name=USB%20Camera
```
Status (REST):

```
GET /status
```

WebSocket events:

```
GET /ws
```

Each `set/get/reset/capabilities` call broadcasts a JSON message, and a `status`
message is pushed every 2 seconds:

```json
{
  "type": "commandResult",
  "command": "set",
  "ok": true,
  "statusCode": 200,
  "exitCode": 0,
  "output": "OK",
  "error": ""
}
```

Status message example:

```json
{
  "type": "status",
  "ok": true,
  "status": "ready",
  "cameraFound": true,
  "cameraName": "USB Camera",
  "cameraIndex": 0,
  "wsClients": 1,
  "appVersion": "v1.0.0",
  "timestamp": "2026-01-23T12:34:56.0000000Z"
}
```

WebSocket commands (optional):

```json
{
  "command": "set",
  "domain": "cam",
  "property": "Focus",
  "value": "200",
  "cameraName": "USB Camera"
}
```

Example (browser):

```javascript
const ws = new WebSocket("ws://127.0.0.1:3790/ws");
ws.onmessage = (event) => {
  const data = JSON.parse(event.data);
  if (data.type === "commandResult" && data.command === "set") {
    console.log("focus status", data.statusCode, data.output);
  }
};
```

If WebSocket fails or is blocked, use the REST endpoints as a fallback.

You can also target a specific index:

```
POST /api/camera/{index}/set
POST /api/camera/{index}/get
POST /api/camera/{index}/reset
GET  /api/camera/{index}/capabilities
```

## Configuration

All settings are optional and read from environment variables:

- `DUVC_API_PORT` (default: `3790`)
- `DUVC_API_CAMERA_NAME` (default: `USB Camera`)
- `DUVC_API_ALLOWED_ORIGINS` (comma-separated list; default allows all)
- `DUVC_CLI_PATH` (override embedded `duvc-cli.exe` path)

## Camera Property Optimization

See [docs/camera-optimization-integration.md](docs/camera-optimization-integration.md)
for integration documentation on analyzing ROI images and optimizing all
camera properties (Exposure, Focus, Gain, Brightness, Contrast, WhiteBalance,
Sharpness, Saturation, Gamma, Hue, BacklightCompensation) via the API.
Includes JavaScript analysis algorithms, an 8-step optimization pipeline
with parallel execution, and guidance on sequential vs parallel property tuning.

## Notes

- The API only listens on `127.0.0.1` for safety.
- `duvc-cli.exe` is embedded in `duvc-api.exe` and extracted to
  `%ProgramData%\DuvcApi\duvc-cli.exe` at runtime.
- The tray menu includes **Show Log** for a real-time API/cli log window,
  with a small command panel that can send REST or WebSocket camera commands.
- The tray menu also includes **Install Camera API as Service** and **Uninstall Camera API Service**,
  which prompt for admin elevation if needed.