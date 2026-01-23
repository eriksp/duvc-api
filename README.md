# duvc-api

Local API + tray indicator for controlling a USB camera using the bundled `duvc-cli.exe`.
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

Uninstall:

```powershell
.\dist\duvc-api.exe uninstall
```

Run service in console mode (useful for debugging):

```powershell
.\dist\duvc-api.exe run
```

## Tray icon

Start the tray indicator only (per-user, no API server):

```powershell
.\dist\duvc-api.exe tray
```

The icon turns green when `USB Camera` is connected and the local API is reachable,
and red if the API is down or the camera is missing.

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
  "value": "200"
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
  "properties": ["Focus", "Exposure"]
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
  "property": "Focus"
}
```

Capabilities:

```
GET /api/usb-camera/capabilities
```

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

## Notes

- The API only listens on `127.0.0.1` for safety.
- `duvc-cli.exe` is embedded in `duvc-api.exe` and extracted to
  `%ProgramData%\DuvcApi\duvc-cli.exe` at runtime.
- The tray menu includes **Show Log** for a real-time API/cli log window.