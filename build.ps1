$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root "src\DuvcApi\Program.cs"
$assemblyInfo = Join-Path $root "src\DuvcApi\AssemblyInfo.cs"
$duvcCli = Join-Path $root "bin\duvc-cli.exe"
$dist = Join-Path $root "dist"
$logoPng = Join-Path $root "dist\assets\cellari_logo.png"
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
    /resource:$logoPng,cellari_logo.png `
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
