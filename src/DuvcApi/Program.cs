using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.ComponentModel;
using System.IO;
using Microsoft.Win32;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DuvcApi
{
    internal static class Program
    {
        public const string ServiceNameConst = "DuvcApi";
        private const string ServiceDisplayName = "Cellari Camera Control API";
        public const string AppTitle = "Cellari Camera Control API";
        private const string DefaultCameraName = "USB Camera";
        private const int DefaultPort = 3790;

        [STAThread]
        public static int Main(string[] args)
        {
            // .NET Framework 4.x defaults to TLS 1.0; GitHub API requires TLS 1.2
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            if (args.Length > 0)
            {
                var command = args[0].Trim().ToLowerInvariant();
                switch (command)
                {
                    case "install":
                        return ServiceInstaller.Install(ServiceNameConst, ServiceDisplayName);
                    case "uninstall":
                        return ServiceInstaller.Uninstall(ServiceNameConst);
                    case "startservice":
                        return ServiceInstaller.StartIfInstalled(ServiceNameConst);
                    case "service":
                        ServiceBaseHost.Run(ServiceNameConst, ServiceDisplayName);
                        return 0;
                    case "tray":
                        TrayApp.Run(false, true);
                        return 0;
                    case "run":
                        ConsoleHost.Run();
                        return 0;
                    case "app":
                        TrayApp.Run(true, true);
                        return 0;
                    case "log":
                        LogApp.Run();
                        return 0;
                    default:
                        Console.Error.WriteLine("Unknown command. Use: install | uninstall | startservice | service | tray | run");
                        return 2;
                }
            }

            if (Environment.UserInteractive)
            {
                TrayApp.Run(true, false);
                return 0;
            }

            ServiceBaseHost.Run(ServiceNameConst, ServiceDisplayName);
            return 0;
        }

        public static string GetCameraName()
        {
            var name = Environment.GetEnvironmentVariable("DUVC_API_CAMERA_NAME");
            return string.IsNullOrWhiteSpace(name) ? DefaultCameraName : name.Trim();
        }

        public static int GetPort()
        {
            var env = Environment.GetEnvironmentVariable("DUVC_API_PORT");
            int port;
            if (int.TryParse(env, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) && port > 0)
            {
                return port;
            }
            return DefaultPort;
        }

        public static string GetVersionLabel()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var info = assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
                if (info != null && info.Length > 0)
                {
                    var attr = info[0] as AssemblyInformationalVersionAttribute;
                    if (attr != null && !string.IsNullOrWhiteSpace(attr.InformationalVersion))
                    {
                        return "v" + attr.InformationalVersion;
                    }
                }
                return "v" + assembly.GetName().Version;
            }
            catch
            {
                return "v1.0.0";
            }
        }
    }

    internal sealed class ConsoleHost
    {
        public static void Run()
        {
            try
            {
                var server = new ApiServer(Program.GetPort(), Program.GetCameraName());
                server.Start();
                Logger.Info("API started in console mode.");

                var trayThread = new Thread(() => TrayApp.Run(false, true))
                {
                    IsBackground = true,
                    Name = "DuvcApiTray"
                };
                trayThread.SetApartmentState(ApartmentState.STA);
                trayThread.Start();

                Thread.Sleep(Timeout.Infinite);
                server.Stop();
                Logger.Info("API stopped.");
            }
            catch (Exception ex)
            {
                Logger.Error("Console host failed: " + ex.Message);
                Console.Error.WriteLine(ex.Message);
            }
        }
    }

    internal static class LogApp
    {
        public static void Run()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new LogForm());
        }
    }

    internal sealed class ServiceBaseHost
    {
        public static void Run(string serviceName, string displayName)
        {
            ServiceBase.Run(new DuvcApiService(serviceName, displayName));
        }
    }

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
                        // The file's mere existence is the signal — contents are
                        // intentionally ignored, so a race between the tray's
                        // WriteAllText and this TryDelete is harmless.
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
                    if (File.Exists(backup))
                    {
                        File.Copy(backup, exe, true);
                        TryDelete(cli);
                        SessionLauncher.TryLaunchInActiveSession(exe, "app");
                    }
                    else
                    {
                        // Worst case: rollback impossible. Surface loudly — the
                        // canonical exe is whatever the failed update left behind.
                        Logger.Error("Rollback failed: backup not present at " + backup);
                    }
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

        // Kills every duvc-api process except this service process. Known
        // limitation: this also matches a concurrent `duvc-api install` or
        // `duvc-api uninstall` invocation (same process name). Re-installing
        // while the watchdog is mid-update is not expected in a kiosk
        // context. Filtering by command line would require WMI /
        // NtQueryInformationProcess and is deferred.
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

    internal sealed class ApiServer
    {
        private readonly int _port;
        private readonly string _cameraName;
        private HttpListener _listener;
        private Thread _listenerThread;
        private volatile bool _running;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private readonly WebSocketHub _webSockets = new WebSocketHub();
        private System.Threading.Timer _statusTimer;
        private int _statusBusy;
        private volatile StatusPayload _cachedStatus;
        private long _cachedStatusTicks;
        private const long CachedStatusMaxAgeMs = 5000;

        public ApiServer(int port, string cameraName)
        {
            _port = port;
            _cameraName = cameraName;
            _webSockets.MessageReceived += OnWebSocketMessage;
        }

        public void Start()
        {
            if (_running)
            {
                return;
            }

            _listener = new HttpListener();
            _listener.Prefixes.Add(string.Format(CultureInfo.InvariantCulture, "http://127.0.0.1:{0}/", _port));
            _listener.Start();
            _running = true;

            _listenerThread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "DuvcApiListener"
            };
            _listenerThread.Start();

            _statusTimer = new System.Threading.Timer(BroadcastStatus, null, 0, 2000);
        }

        public void Stop()
        {
            _running = false;
            try
            {
                if (_listener != null)
                {
                    _listener.Close();
                }
                if (_statusTimer != null)
                {
                    _statusTimer.Dispose();
                }
            }
            catch
            {
                // ignore shutdown errors
            }
        }

        private void ListenLoop()
        {
            while (_running)
            {
                HttpListenerContext context = null;
                try
                {
                    context = _listener.GetContext();
                }
                catch
                {
                    if (!_running)
                    {
                        return;
                    }
                }

                if (context == null)
                {
                    continue;
                }

                ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
            }
        }

        private void HandleRequest(HttpListenerContext context)
        {
            try
            {
                var request = context.Request;
                var response = context.Response;

                ApplyCors(request, response);

                if (request.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    response.StatusCode = 204;
                    response.Close();
                    return;
                }

                var path = request.Url.AbsolutePath.TrimEnd('/');
                if (string.IsNullOrEmpty(path))
                {
                    path = "/";
                }

                string bodyText = null;
                if (request.HasEntityBody)
                {
                    using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
                    {
                        bodyText = reader.ReadToEnd();
                    }
                }

                if (ShouldLogRequest(request.HttpMethod, path))
                {
                    Logger.Info(string.Format(CultureInfo.InvariantCulture, "{0} {1} {2}",
                        request.HttpMethod,
                        path,
                        string.IsNullOrWhiteSpace(bodyText) ? string.Empty : bodyText));
                }

                if (path.Equals("/health", StringComparison.OrdinalIgnoreCase) && request.HttpMethod == "GET")
                {
                    HandleHealth(response);
                    return;
                }

                if (path.Equals("/status", StringComparison.OrdinalIgnoreCase) && request.HttpMethod == "GET")
                {
                    HandleStatus(response);
                    return;
                }

                if (path.Equals("/ws", StringComparison.OrdinalIgnoreCase))
                {
                    HandleWebSocket(context);
                    return;
                }

                if (path.Equals("/api/cameras", StringComparison.OrdinalIgnoreCase) && request.HttpMethod == "GET")
                {
                    HandleListCameras(response);
                    return;
                }

                if (path.Equals("/api/usb-camera/set", StringComparison.OrdinalIgnoreCase) && request.HttpMethod == "POST")
                {
                    HandleSetByName(response, ReadJsonBody<SetRequest>(bodyText));
                    return;
                }

                if (path.Equals("/api/usb-camera/get", StringComparison.OrdinalIgnoreCase) && request.HttpMethod == "POST")
                {
                    HandleGetByName(response, ReadJsonBody<GetRequest>(bodyText));
                    return;
                }

                if (path.Equals("/api/usb-camera/reset", StringComparison.OrdinalIgnoreCase) && request.HttpMethod == "POST")
                {
                    HandleResetByName(response, ReadJsonBody<ResetRequest>(bodyText));
                    return;
                }

                if (path.Equals("/api/usb-camera/capabilities", StringComparison.OrdinalIgnoreCase) && request.HttpMethod == "GET")
                {
                    HandleCapabilitiesByName(request, response);
                    return;
                }

                var cameraIndexMatch = Regex.Match(path, "^/api/camera/(\\d+)/(set|get|reset|capabilities)$", RegexOptions.IgnoreCase);
                if (cameraIndexMatch.Success)
                {
                    var index = int.Parse(cameraIndexMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                    var action = cameraIndexMatch.Groups[2].Value.ToLowerInvariant();
                    switch (action)
                    {
                        case "set":
                            HandleSet(response, index, ReadJsonBody<SetRequest>(bodyText));
                            return;
                        case "get":
                            HandleGet(response, index, ReadJsonBody<GetRequest>(bodyText));
                            return;
                        case "reset":
                            HandleReset(response, index, ReadJsonBody<ResetRequest>(bodyText));
                            return;
                        case "capabilities":
                            HandleCapabilities(response, index);
                            return;
                    }
                }

                WriteJson(response, 404, new { ok = false, error = "Not found" });
            }
            catch (ArgumentException ex)
            {
                Logger.Error("Bad request: " + ex.Message);
                WriteJson(context.Response, 400, new { ok = false, error = ex.Message });
            }
            catch (Exception ex)
            {
                Logger.Error("Server error: " + ex.Message);
                WriteJson(context.Response, 500, new { ok = false, error = ex.Message });
            }
        }

        private void HandleWebSocket(HttpListenerContext context)
        {
            if (!context.Request.IsWebSocketRequest)
            {
                WriteJson(context.Response, 400, new { ok = false, error = "WebSocket connection required." });
                return;
            }

            try
            {
                var wsContext = context.AcceptWebSocketAsync(null).Result;
                _webSockets.AddClient(wsContext.WebSocket);
            }
            catch (Exception ex)
            {
                Logger.Error("WebSocket accept failed: " + ex.Message);
                WriteJson(context.Response, 500, new { ok = false, error = "WebSocket accept failed." });
            }
        }

        private void OnWebSocketMessage(WebSocket socket, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            try
            {
                var command = _json.Deserialize<WebSocketCommand>(message);
                if (command == null || string.IsNullOrWhiteSpace(command.command))
                {
                    SendWebSocketError(socket, "Invalid command.");
                    return;
                }

                if (string.Equals(command.command, "status", StringComparison.OrdinalIgnoreCase))
                {
                    var payload = BuildStatusPayload();
                    _webSockets.Send(socket, _json.Serialize(payload));
                    return;
                }

                int? index = command.cameraIndex;
                if (!index.HasValue)
                {
                    var devices = DuvcCli.ListDevices();
                    var name = string.IsNullOrWhiteSpace(command.cameraName) ? _cameraName : command.cameraName;
                    index = DuvcCli.FindDeviceIndex(devices, name);
                    if (!index.HasValue)
                    {
                        SendWebSocketError(socket, string.Format(CultureInfo.InvariantCulture, "Camera '{0}' not found.", name));
                        return;
                    }
                }

                CommandResponse response;
                switch (command.command.ToLowerInvariant())
                {
                    case "set":
                        response = ExecuteSet(index.Value, command.ToSetRequest());
                        break;
                    case "get":
                        response = ExecuteGet(index.Value, command.ToGetRequest());
                        break;
                    case "reset":
                        response = ExecuteReset(index.Value, command.ToResetRequest());
                        break;
                    case "capabilities":
                        response = ExecuteCapabilities(index.Value);
                        break;
                    default:
                        SendWebSocketError(socket, "Unknown command.");
                        return;
                }

                _webSockets.Send(socket, _json.Serialize(response));
                _webSockets.Broadcast(_json.Serialize(response));
            }
            catch (Exception ex)
            {
                SendWebSocketError(socket, ex.Message);
            }
        }

        private void SendWebSocketError(WebSocket socket, string error)
        {
            var payload = new
            {
                type = "commandResult",
                ok = false,
                statusCode = 500,
                error = error ?? "Unknown error"
            };
            _webSockets.Send(socket, _json.Serialize(payload));
        }

        private void HandleHealth(HttpListenerResponse response)
        {
            try
            {
                var devices = DuvcCli.ListDevices();
                var index = DuvcCli.FindDeviceIndex(devices, _cameraName);
                var cameraFound = index.HasValue;
                var payload = new
                {
                    ok = true,
                    status = cameraFound ? "ready" : "missing",
                    cameraFound,
                    cameraName = _cameraName,
                    cameraIndex = index,
                    appVersion = Program.GetVersionLabel(),
                    devices
                };
                WriteJson(response, cameraFound ? 200 : 503, payload);
            }
            catch (Exception ex)
            {
                WriteJson(response, 503, new { ok = false, error = ex.Message });
            }
        }

        private static bool ShouldLogRequest(string method, string path)
        {
            if (string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return !path.Equals("/health", StringComparison.OrdinalIgnoreCase)
                && !path.Equals("/status", StringComparison.OrdinalIgnoreCase);
        }

        private void HandleStatus(HttpListenerResponse response)
        {
            var cached = _cachedStatus;
            var cachedTicks = Interlocked.Read(ref _cachedStatusTicks);
            var ageMs = (DateTime.UtcNow.Ticks - cachedTicks) / TimeSpan.TicksPerMillisecond;
            var payload = (cached != null && ageMs < CachedStatusMaxAgeMs) ? cached : BuildStatusPayload();
            var statusCode = payload.ok ? (payload.cameraFound ? 200 : 503) : 503;
            WriteJson(response, statusCode, payload);
        }

        private void HandleListCameras(HttpListenerResponse response)
        {
            var devices = DuvcCli.ListDevices();
            WriteJson(response, 200, new { ok = true, devices });
        }

        private void HandleSetByName(HttpListenerResponse response, SetRequest request)
        {
            var name = request != null && !string.IsNullOrWhiteSpace(request.cameraName) ? request.cameraName : _cameraName;
            var index = DuvcCli.FindDeviceIndex(DuvcCli.ListDevices(), name);
            if (!index.HasValue)
            {
                WriteJson(response, 404, new { ok = false, error = string.Format(CultureInfo.InvariantCulture, "Camera '{0}' not found.", name) });
                return;
            }
            HandleSet(response, index.Value, request);
        }

        private void HandleGetByName(HttpListenerResponse response, GetRequest request)
        {
            var name = request != null && !string.IsNullOrWhiteSpace(request.cameraName) ? request.cameraName : _cameraName;
            var index = DuvcCli.FindDeviceIndex(DuvcCli.ListDevices(), name);
            if (!index.HasValue)
            {
                WriteJson(response, 404, new { ok = false, error = string.Format(CultureInfo.InvariantCulture, "Camera '{0}' not found.", name) });
                return;
            }
            HandleGet(response, index.Value, request);
        }

        private void HandleResetByName(HttpListenerResponse response, ResetRequest request)
        {
            var name = request != null && !string.IsNullOrWhiteSpace(request.cameraName) ? request.cameraName : _cameraName;
            var index = DuvcCli.FindDeviceIndex(DuvcCli.ListDevices(), name);
            if (!index.HasValue)
            {
                WriteJson(response, 404, new { ok = false, error = string.Format(CultureInfo.InvariantCulture, "Camera '{0}' not found.", name) });
                return;
            }
            HandleReset(response, index.Value, request);
        }

        private void HandleCapabilitiesByName(HttpListenerRequest request, HttpListenerResponse response)
        {
            var name = request.QueryString["name"];
            if (string.IsNullOrWhiteSpace(name))
            {
                name = _cameraName;
            }
            var index = DuvcCli.FindDeviceIndex(DuvcCli.ListDevices(), name);
            if (!index.HasValue)
            {
                WriteJson(response, 404, new { ok = false, error = string.Format(CultureInfo.InvariantCulture, "Camera '{0}' not found.", name) });
                return;
            }
            HandleCapabilities(response, index.Value);
        }

        private void HandleSet(HttpListenerResponse response, int index, SetRequest request)
        {
            if (request == null)
            {
                WriteJson(response, 400, new { ok = false, error = "Missing request body." });
                return;
            }

            var responsePayload = ExecuteSet(index, request);
            WriteJson(response, responsePayload.statusCode, responsePayload);
            _webSockets.Broadcast(_json.Serialize(responsePayload));
        }

        private void HandleGet(HttpListenerResponse response, int index, GetRequest request)
        {
            if (request == null)
            {
                WriteJson(response, 400, new { ok = false, error = "Missing request body." });
                return;
            }

            var responsePayload = ExecuteGet(index, request);
            WriteJson(response, responsePayload.statusCode, responsePayload);
            _webSockets.Broadcast(_json.Serialize(responsePayload));
        }

        private void HandleReset(HttpListenerResponse response, int index, ResetRequest request)
        {
            if (request == null)
            {
                WriteJson(response, 400, new { ok = false, error = "Missing request body." });
                return;
            }

            var responsePayload = ExecuteReset(index, request);
            WriteJson(response, responsePayload.statusCode, responsePayload);
            _webSockets.Broadcast(_json.Serialize(responsePayload));
        }

        private void HandleCapabilities(HttpListenerResponse response, int index)
        {
            var responsePayload = ExecuteCapabilities(index);
            WriteJson(response, responsePayload.statusCode, responsePayload);
            _webSockets.Broadcast(_json.Serialize(responsePayload));
        }

        private void ApplyCors(HttpListenerRequest request, HttpListenerResponse response)
        {
            var allowed = CorsHelper.GetAllowedOrigin(request.Headers["Origin"]);
            if (!string.IsNullOrEmpty(allowed))
            {
                response.Headers["Access-Control-Allow-Origin"] = allowed;
                response.Headers["Vary"] = "Origin";
            }
            response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
        }

        private T ReadJsonBody<T>(string body) where T : class
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }
            try
            {
                return _json.Deserialize<T>(body);
            }
            catch (InvalidOperationException ex)
            {
                throw new ArgumentException("Invalid JSON body.", ex);
            }
        }

        private void WriteJson(HttpListenerResponse response, int statusCode, object payload)
        {
            var json = _json.Serialize(payload);
            var data = Encoding.UTF8.GetBytes(json);
            response.StatusCode = statusCode;
            response.ContentType = "application/json";
            response.ContentEncoding = Encoding.UTF8;
            response.ContentLength64 = data.Length;
            response.OutputStream.Write(data, 0, data.Length);
            response.OutputStream.Flush();
            response.Close();
        }

        private CommandResponse ExecuteSet(int index, SetRequest request)
        {
            var commandArgs = DuvcCli.BuildSetArguments(index, request);
            var result = DuvcCli.Run(commandArgs);
            var statusCode = result.IsOk ? 200 : 500;
            return new CommandResponse
            {
                type = "commandResult",
                command = "set",
                ok = result.IsOk,
                statusCode = statusCode,
                exitCode = result.ExitCode,
                output = result.StdOut,
                error = result.StdErr
            };
        }

        private CommandResponse ExecuteGet(int index, GetRequest request)
        {
            var commandArgs = DuvcCli.BuildGetArguments(index, request);
            var result = DuvcCli.Run(commandArgs);
            var ok = result.ExitCode == 0;
            return new CommandResponse
            {
                type = "commandResult",
                command = "get",
                ok = ok,
                statusCode = ok ? 200 : 500,
                exitCode = result.ExitCode,
                output = result.StdOut,
                error = result.StdErr
            };
        }

        private CommandResponse ExecuteReset(int index, ResetRequest request)
        {
            var commandArgs = DuvcCli.BuildResetArguments(index, request);
            var result = DuvcCli.Run(commandArgs);
            var ok = result.ExitCode == 0;
            return new CommandResponse
            {
                type = "commandResult",
                command = "reset",
                ok = ok,
                statusCode = ok ? 200 : 500,
                exitCode = result.ExitCode,
                output = result.StdOut,
                error = result.StdErr
            };
        }

        private CommandResponse ExecuteCapabilities(int index)
        {
            var result = DuvcCli.Run(string.Format(CultureInfo.InvariantCulture, "capabilities {0}", index));
            var ok = result.ExitCode == 0;
            return new CommandResponse
            {
                type = "commandResult",
                command = "capabilities",
                ok = ok,
                statusCode = ok ? 200 : 500,
                exitCode = result.ExitCode,
                output = result.StdOut,
                error = result.StdErr
            };
        }

        private StatusPayload BuildStatusPayload()
        {
            StatusPayload payload;
            try
            {
                var devices = DuvcCli.ListDevices(false);
                var index = DuvcCli.FindDeviceIndex(devices, _cameraName);
                var cameraFound = index.HasValue;
                payload = new StatusPayload
                {
                    type = "status",
                    ok = true,
                    status = cameraFound ? "ready" : "missing",
                    cameraFound = cameraFound,
                    cameraName = _cameraName,
                    cameraIndex = index,
                    wsClients = _webSockets.ClientCount,
                    timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                    ,
                    appVersion = Program.GetVersionLabel()
                };
            }
            catch (Exception ex)
            {
                payload = new StatusPayload
                {
                    type = "status",
                    ok = false,
                    status = "error",
                    cameraFound = false,
                    cameraName = _cameraName,
                    cameraIndex = null,
                    wsClients = _webSockets.ClientCount,
                    timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    appVersion = Program.GetVersionLabel(),
                    error = ex.Message
                };
            }

            _cachedStatus = payload;
            Interlocked.Exchange(ref _cachedStatusTicks, DateTime.UtcNow.Ticks);
            return payload;
        }

        private void BroadcastStatus(object state)
        {
            // Skip the periodic duvc-cli enumeration entirely when no one is listening.
            // /status falls back to BuildStatusPayload on demand (with a 5s cache).
            if (_webSockets.ClientCount == 0)
            {
                return;
            }

            if (Interlocked.Exchange(ref _statusBusy, 1) == 1)
            {
                return;
            }

            try
            {
                var payload = BuildStatusPayload();
                _webSockets.Broadcast(_json.Serialize(payload));
            }
            finally
            {
                Interlocked.Exchange(ref _statusBusy, 0);
            }
        }
    }

    internal sealed class WebSocketHub
    {
        private readonly object _sync = new object();
        private readonly List<WebSocket> _clients = new List<WebSocket>();
        public event Action<WebSocket, string> MessageReceived;

        public int ClientCount
        {
            get
            {
                lock (_sync)
                {
                    return _clients.Count;
                }
            }
        }

        public void AddClient(WebSocket socket)
        {
            lock (_sync)
            {
                _clients.Add(socket);
            }

            Task.Run(() => ReceiveLoop(socket));
        }

        public void Send(WebSocket socket, string message)
        {
            if (socket == null || socket.State != WebSocketState.Open)
            {
                return;
            }

            var payload = Encoding.UTF8.GetBytes(message ?? string.Empty);
            try
            {
                socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, CancellationToken.None)
                    .Wait(1000);
            }
            catch
            {
                Remove(socket);
            }
        }

        public void Broadcast(string message)
        {
            byte[] payload = Encoding.UTF8.GetBytes(message ?? string.Empty);
            List<WebSocket> clients;
            lock (_sync)
            {
                clients = new List<WebSocket>(_clients);
            }

            foreach (var client in clients)
            {
                if (client.State != WebSocketState.Open)
                {
                    Remove(client);
                    continue;
                }

                try
                {
                    client.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, CancellationToken.None)
                        .Wait(1000);
                }
                catch
                {
                    Remove(client);
                }
            }
        }

        private void ReceiveLoop(WebSocket socket)
        {
            var buffer = new byte[4096];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var segment = new ArraySegment<byte>(buffer);
                    var result = socket.ReceiveAsync(segment, CancellationToken.None).Result;
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None).Wait(500);
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        string message;
                        if (result.EndOfMessage)
                        {
                            message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        }
                        else
                        {
                            using (var stream = new MemoryStream())
                            {
                                stream.Write(buffer, 0, result.Count);
                                while (!result.EndOfMessage)
                                {
                                    result = socket.ReceiveAsync(segment, CancellationToken.None).Result;
                                    stream.Write(buffer, 0, result.Count);
                                }
                                message = Encoding.UTF8.GetString(stream.ToArray());
                            }
                        }

                        var handler = MessageReceived;
                        if (handler != null)
                        {
                            handler(socket, message);
                        }
                    }
                }
            }
            catch
            {
                // ignore receive errors
            }
            finally
            {
                Remove(socket);
            }
        }

        private void Remove(WebSocket socket)
        {
            lock (_sync)
            {
                _clients.Remove(socket);
            }
            try
            {
                socket.Dispose();
            }
            catch
            {
                // ignore dispose errors
            }
        }
    }

    internal static class CorsHelper
    {
        private static readonly string[] DefaultOrigins = new[]
        {
            "http://localhost", "https://localhost",
            "http://127.0.0.1", "https://127.0.0.1",
            "https://colpo.cellari.io", "https://colpo-staging.cellari.io"
        };

        public static string GetAllowedOrigin(string origin)
        {
            if (string.IsNullOrWhiteSpace(origin))
            {
                return string.Empty;
            }

            var configured = Environment.GetEnvironmentVariable("DUVC_API_ALLOWED_ORIGINS");

            // Use configured origins if set, otherwise allow localhost only
            var allowed = string.IsNullOrWhiteSpace(configured) ? DefaultOrigins : ParseOrigins(configured);

            foreach (var entry in allowed)
            {
                // Match origin or origin with port (e.g. http://localhost:5173)
                if (string.Equals(entry, origin, StringComparison.OrdinalIgnoreCase)
                    || (origin.StartsWith(entry + ":", StringComparison.OrdinalIgnoreCase)
                        && origin.IndexOf(':', entry.Length + 1) < 0))
                {
                    return origin;
                }
            }

            return string.Empty;
        }

        private static string[] ParseOrigins(string configured)
        {
            var parts = configured.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                parts[i] = parts[i].Trim();
            }
            return parts;
        }
    }

    internal sealed class SetRequest
    {
        public string cameraName { get; set; }
        public string domain { get; set; }
        public string property { get; set; }
        public string value { get; set; }
        public string mode { get; set; }
        public bool relative { get; set; }
    }

    internal sealed class GetRequest
    {
        public string cameraName { get; set; }
        public string domain { get; set; }
        public string[] properties { get; set; }
        public bool json { get; set; }

        public GetRequest()
        {
            json = true;
        }
    }

    internal sealed class ResetRequest
    {
        public string cameraName { get; set; }
        public string domain { get; set; }
        public string property { get; set; }
        public bool all { get; set; }
    }

    internal sealed class DuvcCliResult
    {
        public int ExitCode { get; set; }
        public string StdOut { get; set; }
        public string StdErr { get; set; }
        public bool IsOk
        {
            get
            {
                return ExitCode == 0 && StdOut != null && StdOut.IndexOf("OK", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
    }

    internal sealed class CameraDevice
    {
        public int index { get; set; }
        public string name { get; set; }
    }

    internal static class DuvcCli
    {
        private static readonly Regex DeviceRegex = new Regex("^\\[(\\d+)\\]\\s+(.+)$", RegexOptions.Compiled);
        private static readonly Regex SafeArgRegex = new Regex("^[a-zA-Z0-9_\\-\\.]+$", RegexOptions.Compiled);
        private static readonly Regex SafeValueRegex = new Regex("^[a-zA-Z0-9_\\-\\.,]+$", RegexOptions.Compiled);

        private static void ValidateArg(string value, string name)
        {
            if (!SafeArgRegex.IsMatch(value))
            {
                throw new ArgumentException(string.Format(CultureInfo.InvariantCulture,
                    "{0} contains invalid characters.", name));
            }
        }

        private static void ValidateValue(string value, string name)
        {
            if (!SafeValueRegex.IsMatch(value))
            {
                throw new ArgumentException(string.Format(CultureInfo.InvariantCulture,
                    "{0} contains invalid characters.", name));
            }
        }

        public static List<CameraDevice> ListDevices()
        {
            return ListDevices(true);
        }

        public static List<CameraDevice> ListDevices(bool log)
        {
            var result = Run("list", log);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException("duvc-cli list failed: " + result.StdErr);
            }

            var devices = new List<CameraDevice>();
            using (var reader = new StringReader(result.StdOut ?? string.Empty))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var match = DeviceRegex.Match(line.Trim());
                    if (!match.Success)
                    {
                        continue;
                    }

                    devices.Add(new CameraDevice
                    {
                        index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                        name = match.Groups[2].Value.Trim()
                    });
                }
            }

            return devices;
        }

        public static int? FindDeviceIndex(IEnumerable<CameraDevice> devices, string name)
        {
            foreach (var device in devices)
            {
                if (string.Equals(device.name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return device.index;
                }
            }
            return null;
        }

        public static string BuildSetArguments(int index, SetRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.domain) || string.IsNullOrWhiteSpace(request.property))
            {
                throw new ArgumentException("domain and property are required.");
            }

            ValidateArg(request.domain, "domain");
            ValidateArg(request.property, "property");

            var builder = new StringBuilder();
            if (request.relative)
            {
                builder.Append("set --relative ");
            }
            else
            {
                builder.Append("set ");
            }

            builder.Append(index).Append(' ')
                .Append(request.domain).Append(' ')
                .Append(request.property).Append(' ');

            if (!string.IsNullOrWhiteSpace(request.value))
            {
                ValidateValue(request.value, "value");
                builder.Append(request.value);
                if (!string.IsNullOrWhiteSpace(request.mode))
                {
                    ValidateArg(request.mode, "mode");
                    builder.Append(' ').Append(request.mode);
                }
                return builder.ToString().Trim();
            }

            if (!string.IsNullOrWhiteSpace(request.mode))
            {
                ValidateArg(request.mode, "mode");
                builder.Append(request.mode);
                return builder.ToString().Trim();
            }

            throw new ArgumentException("value or mode is required.");
        }

        public static string BuildGetArguments(int index, GetRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.domain) || request.properties == null || request.properties.Length == 0)
            {
                throw new ArgumentException("domain and properties are required.");
            }

            ValidateArg(request.domain, "domain");
            foreach (var prop in request.properties)
            {
                ValidateArg(prop, "property");
            }

            var props = string.Join(",", request.properties);
            var builder = new StringBuilder();
            builder.Append("get ").Append(index).Append(' ')
                .Append(request.domain).Append(' ')
                .Append(props);

            if (request.json)
            {
                builder.Append(" --json");
            }

            return builder.ToString();
        }

        public static string BuildResetArguments(int index, ResetRequest request)
        {
            if (request.all)
            {
                return string.Format(CultureInfo.InvariantCulture, "reset {0} all", index);
            }

            if (string.IsNullOrWhiteSpace(request.domain) || string.IsNullOrWhiteSpace(request.property))
            {
                throw new ArgumentException("domain and property are required when all=false.");
            }

            ValidateArg(request.domain, "domain");
            ValidateArg(request.property, "property");

            return string.Format(CultureInfo.InvariantCulture, "reset {0} {1} {2}", index, request.domain, request.property);
        }

        public static DuvcCliResult Run(string arguments)
        {
            return Run(arguments, true);
        }

        public static DuvcCliResult Run(string arguments, bool log)
        {
            var exePath = ResolveExecutablePath();
            if (log)
            {
                Logger.Info("duvc-cli " + arguments);
            }
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                process.Start();
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(15000))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                        // ignore kill errors
                    }

                    return new DuvcCliResult
                    {
                        ExitCode = -1,
                        StdOut = stdout,
                        StdErr = "duvc-cli timed out"
                    };
                }

                if (log && !string.IsNullOrWhiteSpace(stderr))
                {
                    Logger.Error(stderr.Trim());
                }
                // Skip stdout logging on success — the body is returned to the caller anyway
                // and writing it to disk per request is the main I/O cost on busy frontends.
                if (log && process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stdout))
                {
                    Logger.Info(stdout.Trim());
                }

                return new DuvcCliResult
                {
                    ExitCode = process.ExitCode,
                    StdOut = stdout == null ? null : stdout.Trim(),
                    StdErr = stderr == null ? null : stderr.Trim()
                };
            }
        }

        private static string ResolveExecutablePath()
        {
            var configured = Environment.GetEnvironmentVariable("DUVC_CLI_PATH");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            {
                return configured;
            }

            // Extract duvc-cli.exe next to the running duvc-api.exe so both
            // files stay together (e.g. C:\Kiosk on a kiosk install).
            var baseDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
            var exePath = Path.Combine(baseDir, "duvc-cli.exe");

            if (!File.Exists(exePath))
            {
                Directory.CreateDirectory(baseDir);
                ExtractEmbeddedResource("duvc-cli.exe", exePath);
            }

            return exePath;
        }

        private static void ExtractEmbeddedResource(string resourceName, string targetPath)
        {
            var assembly = Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    throw new FileNotFoundException("Embedded duvc-cli.exe not found.");
                }

                using (var file = File.OpenWrite(targetPath))
                {
                    stream.CopyTo(file);
                }
            }
        }
    }

    internal sealed class TrayInstanceGuard
    {
        private const string MutexName = "Global\\CellariCameraControlTray";
        private Mutex _mutex;
        public bool IsOwner { get; private set; }

        public bool Acquire()
        {
            try
            {
                bool createdNew;
                _mutex = new Mutex(true, MutexName, out createdNew);
                IsOwner = createdNew;
                return createdNew;
            }
            catch
            {
                IsOwner = false;
                return false;
            }
        }

        public void Release()
        {
            try
            {
                if (_mutex != null && IsOwner)
                {
                    _mutex.ReleaseMutex();
                    _mutex.Dispose();
                    _mutex = null;
                }
            }
            catch
            {
                // ignore release errors
            }
        }

        public static bool IsTrayRunning()
        {
            try
            {
                Mutex.OpenExisting(MutexName).Close();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    internal static class ServiceInstaller
    {
        private const string TrayTaskName = "CellariCameraControlTray";
        private const string RunRegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "CellariCameraControl";

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

        public static int Uninstall(string serviceName)
        {
            RunSc(string.Format(CultureInfo.InvariantCulture, "stop {0}", serviceName));
            RemoveLegacyAppTask();
            RemoveUrlAcl(Program.GetPort());
            return RunSc(string.Format(CultureInfo.InvariantCulture, "delete {0}", serviceName));
        }

        // Elevated entry point: start the service if installed.
        // Called by the Control Panel "Start Watchdog" button via RunElevated("startservice").
        public static int StartIfInstalled(string serviceName)
        {
            var status = ServiceStatusHelper.GetStatus(serviceName);
            if (!status.IsInstalled)
            {
                Console.Error.WriteLine("Service is not installed.");
                return 1;
            }
            if (status.IsRunning)
            {
                Console.WriteLine("Service is already running.");
                return 0;
            }
            return RunSc(string.Format(CultureInfo.InvariantCulture, "start {0}", serviceName));
        }

        private static int RunSc(string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var process = Process.Start(startInfo))
            {
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit(10000);

                if (process.ExitCode != 0)
                {
                    Console.Error.WriteLine(stdout);
                    Console.Error.WriteLine(stderr);
                }

                return process.ExitCode;
            }
        }

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

        private static void RemoveLegacyScheduledTask()
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = string.Format(CultureInfo.InvariantCulture, "/Delete /F /TN \"{0}\"", TrayTaskName),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            try
            {
                using (var process = Process.Start(startInfo))
                {
                    process.StandardOutput.ReadToEnd();
                    process.StandardError.ReadToEnd();
                    process.WaitForExit(10000);
                }
            }
            catch
            {
                // ignore cleanup failures (task may not exist)
            }
        }

        private static void ConfigureRecovery(string serviceName)
        {
            // restart after 5s, 10s, 30s; reset failure count after 24h
            RunSc(string.Format(CultureInfo.InvariantCulture,
                "failure {0} reset= 86400 actions= restart/5000/restart/10000/restart/30000",
                serviceName));
        }

        private static void RegisterUrlAcl(int port)
        {
            var url = string.Format(CultureInfo.InvariantCulture, "http://127.0.0.1:{0}/", port);
            // Remove existing ACL first (ignore errors if not present)
            RunNetsh(string.Format(CultureInfo.InvariantCulture, "http delete urlacl url={0}", url));
            RunNetsh(string.Format(CultureInfo.InvariantCulture, "http add urlacl url={0} user=Everyone", url));
        }

        private static void RemoveUrlAcl(int port)
        {
            var url = string.Format(CultureInfo.InvariantCulture, "http://127.0.0.1:{0}/", port);
            RunNetsh(string.Format(CultureInfo.InvariantCulture, "http delete urlacl url={0}", url));
        }

        private static int RunNetsh(string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var process = Process.Start(startInfo))
            {
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit(10000);
                return process.ExitCode;
            }
        }
    }

    internal sealed class TrayApp : ApplicationContext
    {
        private static readonly TrayInstanceGuard InstanceGuard = new TrayInstanceGuard();
        private readonly NotifyIcon _notifyIcon;
        private readonly System.Windows.Forms.Timer _timer;
        private Icon _okIcon;
        private Icon _warnIcon;
        private Icon _wsIcon;
        private Icon _badIcon;
        private string _lastTooltip;
        private LogForm _logForm;
        private readonly ApiServer _server;
        private ToolStripMenuItem _installServiceItem;
        private ToolStripMenuItem _uninstallServiceItem;
        private Form _menuHost;
        private AutoUpdater _updater;
        private ToolStripMenuItem _updateItem;
        private System.Threading.Timer _updateCheckTimer;
        private volatile bool _firstUpdateCheckDone;
        private readonly object _statusLock = new object();
        private StatusResult _lastStatus;
        private DateTime _lastStatusAt;
        private ControlPanelForm _controlPanel;

        private TrayApp(bool startServer)
        {
            if (!InstanceGuard.Acquire())
            {
                return;
            }

            if (startServer)
            {
                try
                {
                    _server = new ApiServer(Program.GetPort(), Program.GetCameraName());
                    _server.Start();
                    Logger.Info("API started in tray mode.");
                }
                catch (Exception ex)
                {
                    Logger.Error("Tray API start failed: " + ex.Message);
                    MessageBox.Show("Failed to start API: " + ex.Message, "DUVC API", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }

            TryStartWatchdogIfInstalled();

            _okIcon = TrayIconFactory.CreateStatusIcon(Color.FromArgb(0, 200, 0));
            _warnIcon = TrayIconFactory.CreateStatusIcon(Color.FromArgb(220, 180, 0));
            _wsIcon = TrayIconFactory.CreateStatusIcon(Color.FromArgb(0, 120, 215));
            _badIcon = TrayIconFactory.CreateStatusIcon(Color.FromArgb(200, 0, 0));

            _notifyIcon = new NotifyIcon
            {
                Icon = _badIcon,
                Visible = true,
                Text = Program.AppTitle
            };

            var menu = new ContextMenuStrip();
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = false;

            var appTitle = new ToolStripMenuItem(string.Format(CultureInfo.InvariantCulture, "{0} {1}", Program.AppTitle, Program.GetVersionLabel()))
            {
                Enabled = false
            };
            var controlPanel = new ToolStripMenuItem("Show Control Panel");
            controlPanel.Click += (sender, args) => ShowControlPanel();
            _installServiceItem = new ToolStripMenuItem("Install Camera API as Service");
            _installServiceItem.Click += (sender, args) => RunElevated("install");
            _uninstallServiceItem = new ToolStripMenuItem("Uninstall Camera API Service");
            _uninstallServiceItem.Click += (sender, args) => RunElevated("uninstall");
            _updateItem = new ToolStripMenuItem("Checking for updates…") { Enabled = false };
            _updateItem.Click += (sender, args) => OnUpdateClicked();
            var exit = new ToolStripMenuItem("Exit");
            exit.Click += (sender, args) => ExitThread();
            menu.Items.Add(appTitle);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(controlPanel);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_installServiceItem);
            menu.Items.Add(_uninstallServiceItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_updateItem);
            menu.Items.Add(exit);
            _notifyIcon.ContextMenuStrip = menu;
            _notifyIcon.MouseUp += (sender, args) =>
            {
                if (args.Button == MouseButtons.Left)
                {
                    ShowContextMenu();
                }
            };
            _notifyIcon.DoubleClick += (sender, args) => ShowControlPanel();

            _timer = new System.Windows.Forms.Timer { Interval = 2000 };
            _timer.Tick += (sender, args) => UpdateStatus();
            _timer.Start();
            UpdateStatus();

            _notifyIcon.BalloonTipTitle = Program.AppTitle;
            _notifyIcon.BalloonTipText = string.Format(CultureInfo.InvariantCulture,
                "Camera API running on port {0}", Program.GetPort());
            _notifyIcon.ShowBalloonTip(3000);

            _updater = new AutoUpdater();
            // Check-only polling for the tray menu: first check after 60 s, then hourly.
            // The installed service (not this process) performs auto-apply.
            _updateCheckTimer = new System.Threading.Timer(
                OnUpdateCheckTick, null, TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(60));
        }

        public static void Run(bool startServer, bool silent)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var app = new TrayApp(startServer);
            if (!InstanceGuard.IsOwner)
            {
                if (!silent)
                {
                    var result = MessageBox.Show(
                        "Another instance is already running. Stop it and start a new one?",
                        Program.AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (result == DialogResult.Yes)
                    {
                        KillOtherInstances();
                        app = new TrayApp(startServer);
                        if (InstanceGuard.IsOwner)
                        {
                            Application.Run(app);
                        }
                    }
                }
                return;
            }
            Application.Run(app);
        }

        private static void KillOtherInstances()
        {
            var currentPid = Process.GetCurrentProcess().Id;
            var myName = Process.GetCurrentProcess().ProcessName;
            foreach (var proc in Process.GetProcessesByName(myName))
            {
                if (proc.Id != currentPid)
                {
                    try
                    {
                        proc.Kill();
                        proc.WaitForExit(5000);
                    }
                    catch { }
                }
                proc.Dispose();
            }
            // Wait for mutex to be released
            Thread.Sleep(1000);
        }

        private void UpdateStatus()
        {
            var status = StatusClient.Check(Program.GetPort());
            lock (_statusLock)
            {
                _lastStatus = status;
                _lastStatusAt = DateTime.UtcNow;
            }
            var tooltip = BuildTooltip(status);
            UpdateServiceMenu(status);
            UpdateUpdateMenu();

            if (!status.ApiReachable)
            {
                SetIcon(_badIcon, tooltip);
            }
            else if (status.WsClients > 0)
            {
                SetIcon(_wsIcon, tooltip);
            }
            else if (status.CameraFound)
            {
                SetIcon(_okIcon, tooltip);
            }
            else
            {
                SetIcon(_warnIcon, tooltip);
            }
        }

        private string BuildTooltip(StatusResult status)
        {
            var rest = status.ApiReachable ? "REST:up" : "REST:down";
            var ws = string.Format(CultureInfo.InvariantCulture, "WS:{0}", status.WsClients);
            var cam = status.CameraFound ? string.Format(CultureInfo.InvariantCulture, "Cam:{0}", status.CameraName) : "Cam:missing";
            var svc = status.ServiceInstalled ? (status.ServiceRunning ? "Svc:running" : "Svc:stopped") : "Svc:not installed";
            var tooltip = string.Format(CultureInfo.InvariantCulture, "{0} {1} {2} {3}", rest, ws, cam, svc);
            return tooltip;
        }

        private void SetIcon(Icon icon, string tooltip)
        {
            if (_notifyIcon.Icon != icon)
            {
                _notifyIcon.Icon = icon;
            }

            if (!string.Equals(_lastTooltip, tooltip, StringComparison.Ordinal))
            {
                _lastTooltip = tooltip;
                _notifyIcon.Text = tooltip.Length > 63 ? tooltip.Substring(0, 63) : tooltip;
            }
        }

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

        // Opportunistic: if the service is installed but stopped, try to start it.
        // Succeeds silently when the user happens to be an admin; otherwise the
        // ServiceController.Start() throws InvalidOperationException (access denied)
        // and the Control Panel's "Start Watchdog" button handles the elevated path.
        private void TryStartWatchdogIfInstalled()
        {
            try
            {
                var status = ServiceStatusHelper.GetStatus(Program.ServiceNameConst);
                if (!status.IsInstalled || status.IsRunning) return;
                using (var sc = new ServiceController(Program.ServiceNameConst))
                {
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(8));
                    Logger.Info("Watchdog service started by standalone tray.");
                }
            }
            catch (Exception ex)
            {
                Logger.Info("Watchdog auto-start skipped: " + ex.Message);
            }
        }

        private void UpdateServiceMenu(StatusResult status)
        {
            if (_installServiceItem != null)
            {
                _installServiceItem.Enabled = !status.ServiceInstalled;
            }
            if (_uninstallServiceItem != null)
            {
                _uninstallServiceItem.Enabled = status.ServiceInstalled;
            }
        }

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

        protected override void ExitThreadCore()
        {
            if (_updateCheckTimer != null)
            {
                _updateCheckTimer.Dispose();
                _updateCheckTimer = null;
            }
            _timer.Stop();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            if (_menuHost != null)
            {
                _menuHost.Close();
                _menuHost.Dispose();
                _menuHost = null;
            }
            if (_okIcon != null)
            {
                _okIcon.Dispose();
            }
            if (_warnIcon != null)
            {
                _warnIcon.Dispose();
            }
            if (_wsIcon != null)
            {
                _wsIcon.Dispose();
            }
            if (_badIcon != null)
            {
                _badIcon.Dispose();
            }
            if (_server != null)
            {
                _server.Stop();
                Logger.Info("API stopped in tray mode.");
            }
            if (_logForm != null && !_logForm.IsDisposed)
            {
                _logForm.Close();
            }
            InstanceGuard.Release();
            base.ExitThreadCore();
        }

        private void ShowLog()
        {
            try
            {
                Logger.Info("Show log requested.");
                if (_logForm == null || _logForm.IsDisposed)
                {
                    _logForm = new LogForm();
                    _logForm.Show();
                }

                if (_logForm.WindowState == FormWindowState.Minimized)
                {
                    _logForm.WindowState = FormWindowState.Normal;
                }
                _logForm.TopMost = true;
                _logForm.BringToFront();
                _logForm.Activate();
                _logForm.TopMost = false;
            }
            catch (Exception ex)
            {
                Logger.Error("Show log failed: " + ex.Message);
                MessageBox.Show("Failed to open log window: " + ex.Message, "DUVC API", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

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

        internal void OnUpdateClickedFromControlPanel()
        {
            OnUpdateClicked();
        }

        internal void ShowLogFromControlPanel()
        {
            ShowLog();
        }

        private void RunElevated(string command)
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule.FileName;
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = command,
                    UseShellExecute = true,
                    Verb = "runas"
                });
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223)
                {
                    Logger.Info("Elevation canceled.");
                    return;
                }
                Logger.Error("Elevation failed: " + ex.Message);
                MessageBox.Show("Failed to run as administrator: " + ex.Message, Program.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                Logger.Error("Elevation failed: " + ex.Message);
                MessageBox.Show("Failed to run as administrator: " + ex.Message, Program.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        internal void RunElevatedFromControlPanel(string command)
        {
            RunElevated(command);
        }

        private void ShowContextMenu()
        {
            var menu = _notifyIcon.ContextMenuStrip;
            if (menu == null)
            {
                return;
            }

            if (_menuHost == null || _menuHost.IsDisposed)
            {
                _menuHost = new Form
                {
                    FormBorderStyle = FormBorderStyle.None,
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual,
                    Size = new Size(1, 1),
                    Opacity = 0,
                    TopMost = true
                };
            }

            _menuHost.Location = Cursor.Position;
            _menuHost.Show();
            _menuHost.Activate();

            menu.Closed -= OnMenuClosed;
            menu.Closed += OnMenuClosed;
            menu.Show(_menuHost, _menuHost.PointToClient(Cursor.Position));
        }

        private void OnMenuClosed(object sender, ToolStripDropDownClosedEventArgs e)
        {
            if (_menuHost != null)
            {
                _menuHost.Hide();
            }
        }
    }

    internal static class TrayIconFactory
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public static Icon CreateStatusIcon(Color color)
        {
            using (var bitmap = new Bitmap(16, 16))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                using (var brush = new SolidBrush(color))
                {
                    graphics.Clear(Color.Transparent);
                    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    graphics.FillEllipse(brush, 2, 2, 12, 12);
                }

                var iconHandle = bitmap.GetHicon();
                try
                {
                    using (var icon = Icon.FromHandle(iconHandle))
                    {
                        return (Icon)icon.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(iconHandle);
                }
            }
        }
    }

    internal sealed class HealthStatus
    {
        public bool ApiReachable { get; set; }
        public bool CameraFound { get; set; }
        public string CameraName { get; set; }
    }

    internal static class HealthClient
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public static HealthStatus Check(int port)
        {
            var result = new HealthStatus
            {
                ApiReachable = false,
                CameraFound = false,
                CameraName = Program.GetCameraName()
            };

            try
            {
                var request = (HttpWebRequest)WebRequest.Create(string.Format(CultureInfo.InvariantCulture, "http://127.0.0.1:{0}/health", port));
                request.Method = "GET";
                request.Timeout = 3000;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    var json = reader.ReadToEnd();
                    var data = Json.Deserialize<Dictionary<string, object>>(json);
                    if (data != null)
                    {
                        result.ApiReachable = true;
                        if (data.ContainsKey("cameraFound"))
                        {
                            result.CameraFound = Convert.ToBoolean(data["cameraFound"], CultureInfo.InvariantCulture);
                        }
                        if (data.ContainsKey("cameraName"))
                        {
                            var nameValue = data["cameraName"];
                            result.CameraName = nameValue == null ? null : nameValue.ToString();
                        }
                    }
                }
            }
            catch
            {
                // API unreachable
            }

            return result;
        }
    }

    internal sealed class StatusResult
    {
        public bool ApiReachable { get; set; }
        public bool CameraFound { get; set; }
        public string CameraName { get; set; }
        public int WsClients { get; set; }
        public bool ServiceInstalled { get; set; }
        public bool ServiceRunning { get; set; }
    }

    internal sealed class HealthSnapshot
    {
        public StatusResult Status { get; set; }
        public DateTime CheckedAt { get; set; }
    }

    internal static class StatusClient
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public static StatusResult Check(int port)
        {
            var result = new StatusResult
            {
                ApiReachable = false,
                CameraFound = false,
                CameraName = Program.GetCameraName(),
                WsClients = 0,
                ServiceInstalled = false,
                ServiceRunning = false
            };

            try
            {
                var request = (HttpWebRequest)WebRequest.Create(string.Format(CultureInfo.InvariantCulture, "http://127.0.0.1:{0}/status", port));
                request.Method = "GET";
                request.Timeout = 3000;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    var json = reader.ReadToEnd();
                    var data = Json.Deserialize<Dictionary<string, object>>(json);
                    if (data != null)
                    {
                        result.ApiReachable = true;
                        if (data.ContainsKey("cameraFound"))
                        {
                            result.CameraFound = Convert.ToBoolean(data["cameraFound"], CultureInfo.InvariantCulture);
                        }
                        if (data.ContainsKey("cameraName"))
                        {
                            var nameValue = data["cameraName"];
                            result.CameraName = nameValue == null ? null : nameValue.ToString();
                        }
                        if (data.ContainsKey("wsClients"))
                        {
                            result.WsClients = Convert.ToInt32(data["wsClients"], CultureInfo.InvariantCulture);
                        }
                    }
                }
            }
            catch
            {
                // API unreachable
            }

            var service = ServiceStatusHelper.GetStatus(Program.ServiceNameConst);
            result.ServiceInstalled = service.IsInstalled;
            result.ServiceRunning = service.IsRunning;

            return result;
        }
    }

    internal sealed class ServiceStatus
    {
        public bool IsInstalled { get; set; }
        public bool IsRunning { get; set; }
    }

    internal static class ServiceStatusHelper
    {
        public static ServiceStatus GetStatus(string serviceName)
        {
            var status = new ServiceStatus { IsInstalled = false, IsRunning = false };
            try
            {
                var services = ServiceController.GetServices();
                foreach (var service in services)
                {
                    if (string.Equals(service.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        status.IsInstalled = true;
                        status.IsRunning = service.Status == ServiceControllerStatus.Running;
                        break;
                    }
                }
            }
            catch
            {
                // ignore service lookup errors
            }
            return status;
        }
    }

    internal sealed class StatusPayload
    {
        public string type { get; set; }
        public bool ok { get; set; }
        public string status { get; set; }
        public bool cameraFound { get; set; }
        public string cameraName { get; set; }
        public int? cameraIndex { get; set; }
        public int wsClients { get; set; }
        public string timestamp { get; set; }
        public string appVersion { get; set; }
        public string error { get; set; }
    }

    internal sealed class CommandResponse
    {
        public string type { get; set; }
        public string command { get; set; }
        public bool ok { get; set; }
        public int statusCode { get; set; }
        public int exitCode { get; set; }
        public string output { get; set; }
        public string error { get; set; }
    }

    internal sealed class WebSocketCommand
    {
        public string command { get; set; }
        public int? cameraIndex { get; set; }
        public string cameraName { get; set; }
        public string domain { get; set; }
        public string property { get; set; }
        public string value { get; set; }
        public string mode { get; set; }
        public bool relative { get; set; }
        public string[] properties { get; set; }
        public bool json { get; set; }
        public bool all { get; set; }

        public SetRequest ToSetRequest()
        {
            return new SetRequest
            {
                domain = domain,
                property = property,
                value = value,
                mode = mode,
                relative = relative
            };
        }

        public GetRequest ToGetRequest()
        {
            var request = new GetRequest();
            request.domain = domain;
            request.properties = properties;
            request.json = json;
            return request;
        }

        public ResetRequest ToResetRequest()
        {
            return new ResetRequest
            {
                domain = domain,
                property = property,
                all = all
            };
        }
    }

    internal static class Logger
    {
        private static readonly object Sync = new object();
        private static readonly Queue<string> Entries = new Queue<string>();
        private const int MaxEntries = 500;
        private static readonly string LogPath = Paths.LogFile;
        private const long MaxLogSizeBytes = 5 * 1024 * 1024;

        public static event Action<string> Logged;

        public static void Info(string message)
        {
            Write("INFO", message);
        }

        public static void Error(string message)
        {
            Write("ERROR", message);
        }

        public static string[] Snapshot()
        {
            lock (Sync)
            {
                return Entries.ToArray();
            }
        }

        private static void Write(string level, string message)
        {
            var line = string.Format(CultureInfo.InvariantCulture, "{0} [{1}] {2}",
                DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                level,
                message ?? string.Empty);

            lock (Sync)
            {
                Entries.Enqueue(line);
                while (Entries.Count > MaxEntries)
                {
                    Entries.Dequeue();
                }

                try
                {
                    var dir = Path.GetDirectoryName(LogPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    if (File.Exists(LogPath))
                    {
                        var info = new FileInfo(LogPath);
                        if (info.Length > MaxLogSizeBytes)
                        {
                            File.WriteAllText(LogPath, string.Empty, Encoding.UTF8);
                        }
                    }

                    File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    // ignore file logging errors
                }
            }

            var handler = Logged;
            if (handler != null)
            {
                handler(line);
            }
        }
    }

    // Launches a process into the active interactive console session from a
    // Session 0 service (LocalSystem holds SE_TCB_NAME, required by WTSQueryUserToken).
    internal static class SessionLauncher
    {
        private const uint INVALID_SESSION = 0xFFFFFFFF;
        private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        private const uint CREATE_NEW_CONSOLE = 0x00000010;
        // TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY — minimum rights
        // required to create a primary token for CreateProcessAsUser.
        private const uint TOKEN_RIGHTS = 0x000B;
        private const int SecurityImpersonation = 2;
        private const int TokenPrimary = 1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpReserved;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpDesktop;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpTitle;
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

                if (!DuplicateTokenEx(userToken, TOKEN_RIGHTS, IntPtr.Zero,
                        SecurityImpersonation, TokenPrimary, out primaryToken))
                {
                    Logger.Error("DuplicateTokenEx failed: " + Marshal.GetLastWin32Error());
                    return false;
                }

                if (!CreateEnvironmentBlock(out envBlock, primaryToken, false))
                {
                    // Non-fatal: the launched process will inherit the service's
                    // Session 0 environment instead. Log so this degraded mode is
                    // observable.
                    Logger.Error("CreateEnvironmentBlock failed: " + Marshal.GetLastWin32Error());
                }

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

        public static string LogFile { get { return Path.Combine(StateDir, "duvc-api.log"); } }

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
        // Volatile so a tray-UI read sees the write from the pool-thread checker
        // without a race on the reference publish.
        private volatile UpdateInfo _availableUpdate;
        public UpdateInfo AvailableUpdate { get { return _availableUpdate; } }

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
                    if (IsNewer(latestVersion, _currentVersion) && string.IsNullOrEmpty(exeUrl))
                    {
                        Logger.Error("Release v" + latestVersion + " has no duvc-api.exe asset.");
                    }
                    _availableUpdate = null;
                    return null;
                }

                _availableUpdate = new UpdateInfo
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
            try
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
            catch (Exception ex)
            {
                Logger.Error("Apply update failed: " + ex.Message);
            }
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

    internal sealed class LogForm : Form
    {
        private readonly TextBox _textBox;
        private readonly Button _modeToggleButton;
        private readonly Button _wsToggleButton;
        private readonly Label _statusLabel;
        private readonly Label _wsLabel;
        private readonly System.Windows.Forms.Timer _statusTimer;
        private readonly ComboBox _cameraCombo;
        private readonly Button _refreshButton;
        private readonly FlowLayoutPanel _capabilityPanel;
        private readonly List<CapabilityItem> _capabilities = new List<CapabilityItem>();
        private readonly List<CameraDevice> _cameraDevices = new List<CameraDevice>();
        private ClientWebSocket _wsClient;
        private CancellationTokenSource _wsCts;
        private bool _useWebSocket;

        public LogForm()
        {
            Text = "Cellari Camera Control API - Log";
            Width = 860;
            Height = 540;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            WindowState = FormWindowState.Normal;

            _textBox = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                Dock = DockStyle.Fill,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9f),
                BackColor = Color.White
            };

            var topPanel = new Panel { Dock = DockStyle.Top, Height = 80 };
            _capabilityPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 240,
                AutoScroll = true,
                WrapContents = false,
                FlowDirection = FlowDirection.TopDown
            };
            var logPanel = new Panel { Dock = DockStyle.Fill };
            logPanel.Controls.Add(_textBox);
            Controls.Add(logPanel);
            Controls.Add(_capabilityPanel);
            Controls.Add(topPanel);

            foreach (var line in Logger.Snapshot())
            {
                AppendLine(line);
            }

            _statusLabel = new Label
            {
                AutoSize = true,
                Location = new Point(10, 8),
                Text = "REST: unknown"
            };
            _wsLabel = new Label
            {
                AutoSize = true,
                Location = new Point(160, 8),
                Text = "WS: disconnected"
            };

            _modeToggleButton = new Button
            {
                Text = "Mode: REST",
                Location = new Point(10, 28),
                Width = 110
            };
            _modeToggleButton.Click += (sender, args) => ToggleMode();

            _wsToggleButton = new Button
            {
                Text = "Connect WS",
                Location = new Point(130, 28),
                Width = 100
            };
            _wsToggleButton.Click += (sender, args) => ToggleWebSocket();

            var cameraLabel = new Label
            {
                AutoSize = true,
                Location = new Point(290, 8),
                Text = "Camera:"
            };
            _cameraCombo = new ComboBox
            {
                Location = new Point(350, 6),
                Width = 250,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cameraCombo.SelectedIndexChanged += (sender, args) => RefreshCapabilities();

            _refreshButton = new Button
            {
                Text = "Refresh List",
                Location = new Point(610, 4),
                Width = 90
            };
            _refreshButton.Click += (sender, args) => RefreshCameraList();

            topPanel.Controls.Add(_statusLabel);
            topPanel.Controls.Add(_wsLabel);
            topPanel.Controls.Add(_modeToggleButton);
            topPanel.Controls.Add(_wsToggleButton);
            topPanel.Controls.Add(cameraLabel);
            topPanel.Controls.Add(_cameraCombo);
            topPanel.Controls.Add(_refreshButton);

            Logger.Logged += OnLogged;
            FormClosed += OnClosed;

            _statusTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _statusTimer.Tick += (sender, args) => UpdateStatus();
            _statusTimer.Start();
            UpdateStatus();
            Shown += (sender, args) => BeginInvoke(new Action(RefreshCameraList));

            _useWebSocket = SettingsStore.LoadUseWebSocket();
            ApplyModeLabel();
        }

        private void OnLogged(string line)
        {
            if (IsDisposed)
            {
                return;
            }

            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(OnLogged), line);
                return;
            }

            AppendLine(line);
        }

        private void AppendLine(string line)
        {
            if (_textBox.TextLength > 0)
            {
                _textBox.AppendText(Environment.NewLine);
            }
            _textBox.AppendText(line);
            _textBox.SelectionStart = _textBox.TextLength;
            _textBox.ScrollToCaret();
        }

        private void OnClosed(object sender, FormClosedEventArgs e)
        {
            Logger.Logged -= OnLogged;
            if (_statusTimer != null)
            {
                _statusTimer.Stop();
            }
            DisconnectWebSocket();
        }

        private void UpdateStatus()
        {
            var status = StatusClient.Check(Program.GetPort());
            _statusLabel.Text = string.Format(CultureInfo.InvariantCulture,
                "REST: {0} | Cam: {1}",
                status.ApiReachable ? "up" : "down",
                status.CameraFound ? status.CameraName : "missing");

            if (_wsClient != null && _wsClient.State == WebSocketState.Open)
            {
                _wsLabel.Text = "WS: connected";
            }
            else
            {
                _wsLabel.Text = "WS: disconnected";
            }
        }

        private void RefreshCameraList()
        {
            try
            {
                _cameraDevices.Clear();
                _cameraDevices.AddRange(ApiClient.ListCameras(Program.GetPort()));
                _cameraCombo.Items.Clear();
                foreach (var device in _cameraDevices)
                {
                    _cameraCombo.Items.Add(string.Format(CultureInfo.InvariantCulture, "[{0}] {1}", device.index, device.name));
                }

                if (_cameraCombo.Items.Count > 0)
                {
                    _cameraCombo.SelectedIndex = 0;
                }
                else
                {
                    _capabilityPanel.Controls.Clear();
                }
            }
            catch (Exception ex)
            {
                AppendLine("List cameras error: " + ex.Message);
            }
        }

        private void RefreshCapabilities()
        {
            try
            {
                var index = GetSelectedCameraIndex();
                if (!index.HasValue)
                {
                    return;
                }

                var output = ApiClient.GetCapabilitiesOutputByIndex(Program.GetPort(), index.Value);
                _capabilities.Clear();
                _capabilities.AddRange(CapabilityParser.Parse(output));
                BuildCapabilityControls();
            }
            catch (Exception ex)
            {
                AppendLine("Capabilities error: " + ex.Message);
            }
        }

        private void BuildCapabilityControls()
        {
            _capabilityPanel.SuspendLayout();
            _capabilityPanel.Controls.Clear();

            AddCapabilityGroup("CAM", "cam");
            AddCapabilityGroup("VID", "vid");
            _capabilityPanel.ResumeLayout();
        }

        private void AddCapabilityGroup(string title, string domain)
        {
            var group = _capabilities.FindAll(c => string.Equals(c.Domain, domain, StringComparison.OrdinalIgnoreCase));
            if (group.Count == 0)
            {
                return;
            }

            var header = new Label
            {
                AutoSize = true,
                Font = new Font(Font.FontFamily, 9f, FontStyle.Bold),
                Text = title
            };
            _capabilityPanel.Controls.Add(header);

            foreach (var capability in group)
            {
                var row = new Panel
                {
                    Width = Math.Max(820, _capabilityPanel.ClientSize.Width - 30),
                    Height = 46
                };

                var label = new Label
                {
                    AutoSize = false,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Location = new Point(6, 12),
                    Width = 180,
                    Text = capability.DisplayName
                };

                var track = new TrackBar
                {
                    Minimum = capability.Min,
                    Maximum = capability.Max,
                    Value = Math.Min(capability.Max, Math.Max(capability.Min, capability.Current)),
                    TickFrequency = Math.Max(1, capability.Step),
                    SmallChange = Math.Max(1, capability.Step),
                    LargeChange = Math.Max(1, capability.Step) * 5,
                    Width = 260,
                    Location = new Point(190, 6)
                };

                var numeric = new NumericUpDown
                {
                    Minimum = capability.Min,
                    Maximum = capability.Max,
                    Value = track.Value,
                    Width = 70,
                    Location = new Point(460, 12)
                };

                var setButton = new Button
                {
                    Text = "Set",
                    Width = 40,
                    Location = new Point(540, 10)
                };

                var autoButton = new Button
                {
                    Text = "Auto",
                    Width = 50,
                    Location = new Point(585, 10)
                };

                var manualButton = new Button
                {
                    Text = "Manual",
                    Width = 60,
                    Location = new Point(640, 10)
                };

                var getButton = new Button
                {
                    Text = "Get",
                    Width = 45,
                    Location = new Point(705, 10)
                };

                var resetButton = new Button
                {
                    Text = "Reset",
                    Width = 50,
                    Location = new Point(755, 10)
                };

                var updating = false;
                track.ValueChanged += (sender, args) =>
                {
                    if (updating) return;
                    updating = true;
                    numeric.Value = track.Value;
                    updating = false;
                };
                numeric.ValueChanged += (sender, args) =>
                {
                    if (updating) return;
                    updating = true;
                    track.Value = (int)numeric.Value;
                    updating = false;
                };

                setButton.Click += (sender, args) => SendSet(capability, track.Value);
                autoButton.Click += (sender, args) => SendMode(capability, "auto");
                manualButton.Click += (sender, args) => SendMode(capability, "manual");
                getButton.Click += (sender, args) => SendGet(capability);
                resetButton.Click += (sender, args) => SendReset(capability);

                row.Controls.Add(label);
                row.Controls.Add(track);
                row.Controls.Add(numeric);
                row.Controls.Add(setButton);
                row.Controls.Add(autoButton);
                row.Controls.Add(manualButton);
                row.Controls.Add(getButton);
                row.Controls.Add(resetButton);

                _capabilityPanel.Controls.Add(row);
            }
        }

        private void SendSet(CapabilityItem capability, int value)
        {
            if (capability == null)
            {
                return;
            }

            var request = new SetRequest
            {
                domain = capability.Domain,
                property = capability.Name,
                value = value.ToString(CultureInfo.InvariantCulture),
                mode = null,
                relative = false
            };
            SendCommand("set", request, null);
        }

        private void SendMode(CapabilityItem capability, string mode)
        {
            if (capability == null)
            {
                return;
            }

            var request = new SetRequest
            {
                domain = capability.Domain,
                property = capability.Name,
                mode = mode,
                relative = false
            };
            SendCommand("set", request, null);
        }

        private void SendGet(CapabilityItem capability)
        {
            if (capability == null)
            {
                return;
            }

            var request = new WebSocketCommand
            {
                command = "get",
                domain = capability.Domain,
                properties = new[] { capability.Name },
                json = true
            };
            SendCommand("get", null, request);
        }

        private void SendReset(CapabilityItem capability)
        {
            if (capability == null)
            {
                return;
            }

            var request = new WebSocketCommand
            {
                command = "reset",
                domain = capability.Domain,
                property = capability.Name,
                all = false
            };
            SendCommand("reset", null, request);
        }

        private void SendCommand(string command, SetRequest setRequest, WebSocketCommand wsRequest)
        {
            var index = GetSelectedCameraIndex();
            if (!index.HasValue)
            {
                AppendLine("No camera selected.");
                return;
            }

            if (_useWebSocket)
            {
                if (!EnsureWebSocketConnected())
                {
                    AppendLine("WebSocket not connected.");
                    return;
                }

                WebSocketCommand payload;
                if (wsRequest != null)
                {
                    payload = wsRequest;
                    payload.cameraIndex = index;
                }
                else
                {
                    payload = new WebSocketCommand
                    {
                        command = command,
                        cameraIndex = index,
                        domain = setRequest.domain,
                        property = setRequest.property,
                        value = setRequest.value,
                        mode = setRequest.mode,
                        relative = setRequest.relative
                    };
                }

                SendWebSocket(payload);
            }
            else
            {
                RestCommandResult result;
                switch (command)
                {
                    case "set":
                        result = ApiClient.SendSetByIndex(Program.GetPort(), index.Value, setRequest);
                        break;
                    case "get":
                        result = ApiClient.SendGetByIndex(Program.GetPort(), index.Value, wsRequest.ToGetRequest());
                        break;
                    case "reset":
                        result = ApiClient.SendResetByIndex(Program.GetPort(), index.Value, wsRequest.ToResetRequest());
                        break;
                    default:
                        return;
                }

                var requestLabel = BuildRequestLabel(command, setRequest, wsRequest);
                AppendLine(string.Format(CultureInfo.InvariantCulture, "REST {0} -> {1} {2}", requestLabel, result.statusCode, result.output));
            }
        }

        private string BuildRequestLabel(string command, SetRequest setRequest, WebSocketCommand wsRequest)
        {
            if (command == "set" && setRequest != null)
            {
                return string.Format(CultureInfo.InvariantCulture, "set {0} {1}", setRequest.domain, setRequest.property);
            }
            if (command == "get" && wsRequest != null)
            {
                var props = wsRequest.properties == null ? string.Empty : string.Join(",", wsRequest.properties);
                return string.Format(CultureInfo.InvariantCulture, "get {0} {1}", wsRequest.domain, props);
            }
            if (command == "reset" && wsRequest != null)
            {
                return string.Format(CultureInfo.InvariantCulture, "reset {0} {1}", wsRequest.domain, wsRequest.property);
            }
            return command;
        }

        private void ToggleWebSocket()
        {
            if (_wsClient != null && _wsClient.State == WebSocketState.Open)
            {
                DisconnectWebSocket();
                return;
            }
            EnsureWebSocketConnected();
        }

        private bool EnsureWebSocketConnected()
        {
            if (_wsClient != null && _wsClient.State == WebSocketState.Open)
            {
                return true;
            }

            try
            {
                _wsCts = new CancellationTokenSource();
                _wsClient = new ClientWebSocket();
                _wsClient.ConnectAsync(new Uri(string.Format(CultureInfo.InvariantCulture, "ws://127.0.0.1:{0}/ws", Program.GetPort())), _wsCts.Token).Wait(3000);
                _wsToggleButton.Text = "Disconnect WS";
                Task.Run(() => ReceiveWebSocketLoop(_wsClient, _wsCts.Token));
                return true;
            }
            catch (Exception ex)
            {
                AppendLine("WebSocket connect failed: " + ex.Message);
                DisconnectWebSocket();
                return false;
            }
        }

        private void DisconnectWebSocket()
        {
            try
            {
                if (_wsCts != null)
                {
                    _wsCts.Cancel();
                }
                if (_wsClient != null)
                {
                    _wsClient.Dispose();
                }
            }
            catch
            {
                // ignore disconnect errors
            }
            finally
            {
                _wsClient = null;
                _wsCts = null;
                _wsToggleButton.Text = "Connect WS";
            }
        }

        private void SendWebSocket(WebSocketCommand command)
        {
            try
            {
                var json = new JavaScriptSerializer().Serialize(command);
                var payload = Encoding.UTF8.GetBytes(json);
                _wsClient.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, CancellationToken.None).Wait(1000);
            }
            catch (Exception ex)
            {
                AppendLine("WebSocket send failed: " + ex.Message);
            }
        }

        private void ReceiveWebSocketLoop(ClientWebSocket socket, CancellationToken token)
        {
            var buffer = new byte[4096];
            try
            {
                while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    var segment = new ArraySegment<byte>(buffer);
                    var result = socket.ReceiveAsync(segment, token).Result;
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    if (InvokeRequired)
                    {
                        BeginInvoke(new Action<string>(AppendLine), message);
                    }
                    else
                    {
                        AppendLine(message);
                    }
                }
            }
            catch
            {
                // ignore receive errors
            }
            finally
            {
                if (InvokeRequired)
                {
                    BeginInvoke(new Action(DisconnectWebSocket));
                }
                else
                {
                    DisconnectWebSocket();
                }
            }
        }

        private void ToggleMode()
        {
            _useWebSocket = !_useWebSocket;
            ApplyModeLabel();
            SettingsStore.SaveUseWebSocket(_useWebSocket);

            if (_useWebSocket)
            {
                EnsureWebSocketConnected();
            }
            else
            {
                DisconnectWebSocket();
            }
        }

        private void ApplyModeLabel()
        {
            _modeToggleButton.Text = _useWebSocket ? "Mode: WS" : "Mode: REST";
        }

        private int? GetSelectedCameraIndex()
        {
            if (_cameraCombo.SelectedIndex < 0 || _cameraCombo.SelectedIndex >= _cameraDevices.Count)
            {
                return null;
            }
            return _cameraDevices[_cameraCombo.SelectedIndex].index;
        }
    }

    internal static class SettingsStore
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "DuvcApi",
            "settings.json");

        public static bool LoadUseWebSocket()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                {
                    return false;
                }

                var json = File.ReadAllText(SettingsPath, Encoding.UTF8);
                var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                if (data != null && data.ContainsKey("useWebSocket"))
                {
                    return Convert.ToBoolean(data["useWebSocket"], CultureInfo.InvariantCulture);
                }
            }
            catch
            {
                // ignore settings errors
            }

            return false;
        }

        public static void SaveUseWebSocket(bool useWebSocket)
        {
            try
            {
                var dir = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var json = new JavaScriptSerializer().Serialize(new Dictionary<string, object>
                {
                    { "useWebSocket", useWebSocket }
                });
                File.WriteAllText(SettingsPath, json, Encoding.UTF8);
            }
            catch
            {
                // ignore settings errors
            }
        }
    }

    internal sealed class CapabilityItem
    {
        public string Domain { get; set; }
        public string Name { get; set; }
        public int Min { get; set; }
        public int Max { get; set; }
        public int Step { get; set; }
        public int Default { get; set; }
        public int Current { get; set; }
        public string Mode { get; set; }

        public string DisplayName
        {
            get
            {
                return string.Format(CultureInfo.InvariantCulture, "{0} {1}", Domain.ToUpperInvariant(), Name);
            }
        }
    }

    internal static class CapabilityParser
    {
        private static readonly Regex LineRegex = new Regex("^\\s*(CAM|VID)\\s+([^:]+):\\s+\\[(-?\\d+),(-?\\d+)\\]\\s+step=(-?\\d+)\\s+default=(-?\\d+)\\s+current=(-?\\d+)\\s+\\(([^\\)]+)\\)",
            RegexOptions.Compiled);

        public static List<CapabilityItem> Parse(string output)
        {
            var list = new List<CapabilityItem>();
            if (string.IsNullOrWhiteSpace(output))
            {
                return list;
            }

            using (var reader = new StringReader(output))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var match = LineRegex.Match(line);
                    if (!match.Success)
                    {
                        continue;
                    }

                    list.Add(new CapabilityItem
                    {
                        Domain = match.Groups[1].Value.Trim().ToLowerInvariant(),
                        Name = match.Groups[2].Value.Trim(),
                        Min = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
                        Max = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture),
                        Step = int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture),
                        Default = int.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture),
                        Current = int.Parse(match.Groups[7].Value, CultureInfo.InvariantCulture),
                        Mode = match.Groups[8].Value.Trim()
                    });
                }
            }

            return list;
        }
    }

    internal sealed class RestCommandResult
    {
        public bool ok { get; set; }
        public int statusCode { get; set; }
        public int exitCode { get; set; }
        public string output { get; set; }
        public string error { get; set; }
    }

    internal static class ApiClient
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public static string GetCapabilitiesOutput(int port)
        {
            var response = Get(string.Format(CultureInfo.InvariantCulture, "http://127.0.0.1:{0}/api/usb-camera/capabilities", port));
            var data = Json.Deserialize<Dictionary<string, object>>(response);
            if (data != null && data.ContainsKey("output"))
            {
                return data["output"] == null ? null : data["output"].ToString();
            }
            return null;
        }

        public static string GetCapabilitiesOutputByIndex(int port, int index)
        {
            var response = Get(string.Format(CultureInfo.InvariantCulture, "http://127.0.0.1:{0}/api/camera/{1}/capabilities", port, index));
            var data = Json.Deserialize<Dictionary<string, object>>(response);
            if (data != null && data.ContainsKey("output"))
            {
                return data["output"] == null ? null : data["output"].ToString();
            }
            return null;
        }

        public static List<CameraDevice> ListCameras(int port)
        {
            var response = Get(string.Format(CultureInfo.InvariantCulture, "http://127.0.0.1:{0}/api/cameras", port));
            var data = Json.Deserialize<Dictionary<string, object>>(response);
            if (data != null && data.ContainsKey("devices"))
            {
                var list = new List<CameraDevice>();
                var devices = data["devices"] as object[];
                if (devices == null)
                {
                    var arr = data["devices"] as ArrayList;
                    if (arr != null)
                    {
                        devices = arr.ToArray();
                    }
                }

                if (devices != null)
                {
                    foreach (var entry in devices)
                    {
                        var dict = entry as Dictionary<string, object>;
                        if (dict == null)
                        {
                            continue;
                        }

                        var device = new CameraDevice();
                        if (dict.ContainsKey("index"))
                        {
                            device.index = Convert.ToInt32(dict["index"], CultureInfo.InvariantCulture);
                        }
                        if (dict.ContainsKey("name"))
                        {
                            device.name = dict["name"] == null ? null : dict["name"].ToString();
                        }
                        list.Add(device);
                    }
                }
                return list;
            }
            return new List<CameraDevice>();
        }

        public static RestCommandResult SendSet(int port, SetRequest request)
        {
            return Post(string.Format(CultureInfo.InvariantCulture, "http://127.0.0.1:{0}/api/usb-camera/set", port), request);
        }

        public static RestCommandResult SendSetByIndex(int port, int index, SetRequest request)
        {
            return Post(string.Format(CultureInfo.InvariantCulture, "http://127.0.0.1:{0}/api/camera/{1}/set", port, index), request);
        }

        public static RestCommandResult SendGet(int port, GetRequest request)
        {
            return Post(string.Format(CultureInfo.InvariantCulture, "http://127.0.0.1:{0}/api/usb-camera/get", port), request);
        }

        public static RestCommandResult SendGetByIndex(int port, int index, GetRequest request)
        {
            return Post(string.Format(CultureInfo.InvariantCulture, "http://127.0.0.1:{0}/api/camera/{1}/get", port, index), request);
        }

        public static RestCommandResult SendReset(int port, ResetRequest request)
        {
            return Post(string.Format(CultureInfo.InvariantCulture, "http://127.0.0.1:{0}/api/usb-camera/reset", port), request);
        }

        public static RestCommandResult SendResetByIndex(int port, int index, ResetRequest request)
        {
            return Post(string.Format(CultureInfo.InvariantCulture, "http://127.0.0.1:{0}/api/camera/{1}/reset", port, index), request);
        }

        private static RestCommandResult Post(string url, object payload)
        {
            var json = Json.Serialize(payload);
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Timeout = 3000;
            request.ReadWriteTimeout = 3000;
            var body = Encoding.UTF8.GetBytes(json);
            request.ContentLength = body.Length;
            using (var stream = request.GetRequestStream())
            {
                stream.Write(body, 0, body.Length);
            }

            var responseText = GetResponseText(request);
            var result = Json.Deserialize<Dictionary<string, object>>(responseText);
            return ParseResult(result);
        }

        private static string Get(string url)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = 3000;
            request.ReadWriteTimeout = 3000;
            return GetResponseText(request);
        }

        private static string GetResponseText(HttpWebRequest request)
        {
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream()))
            {
                return reader.ReadToEnd();
            }
        }

        private static RestCommandResult ParseResult(Dictionary<string, object> result)
        {
            var response = new RestCommandResult();
            if (result == null)
            {
                return response;
            }

            if (result.ContainsKey("ok"))
            {
                response.ok = Convert.ToBoolean(result["ok"], CultureInfo.InvariantCulture);
            }
            if (result.ContainsKey("exitCode"))
            {
                response.exitCode = Convert.ToInt32(result["exitCode"], CultureInfo.InvariantCulture);
            }
            if (result.ContainsKey("output"))
            {
                response.output = result["output"] == null ? null : result["output"].ToString();
            }
            if (result.ContainsKey("error"))
            {
                response.error = result["error"] == null ? null : result["error"].ToString();
            }

            response.statusCode = response.ok ? 200 : 500;
            return response;
        }
    }

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
                // Icon(Stream) can defer reading sub-image data for multi-size ICOs whose
                // entries are PNG payloads. Buffer the whole stream and Clone() so the
                // returned Icon owns an independent handle and we can dispose the stream.
                using (var ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    ms.Position = 0;
                    using (var tmp = new Icon(ms))
                    {
                        return (Icon)tmp.Clone();
                    }
                }
            }
        }
    }

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
        private Button _installBtn, _uninstallBtn, _startWatchdogBtn;
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
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(760, 760);
            MinimumSize = new Size(640, 480);
            BackColor = SystemColors.Window;
            Font = new Font("Segoe UI", 9f);

            try { Icon = EmbeddedAssets.LoadIcon("cellari_logo.ico"); }
            catch (Exception ex) { Logger.Error("Control Panel icon load failed: " + ex.Message); }

            // Scrollable host so the form stays usable on small screens / DPI scaling.
            var scrollHost = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = SystemColors.Window
            };

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
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

            scrollHost.Controls.Add(root);
            Controls.Add(scrollHost);

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
            try { pic.Image = EmbeddedAssets.LoadPng("cellari_logo.png"); }
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

            grid.Controls.Add(_apiBullet,      0, 0); grid.Controls.Add(NewBodyLabel("API"),      1, 0); grid.Controls.Add(_apiText,      2, 0);
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
            _startWatchdogBtn = new Button { Text = "Start Watchdog", AutoSize = true, Margin = new Padding(8, 0, 0, 0), Padding = new Padding(8, 2, 8, 2), Visible = false };
            _startWatchdogBtn.Click += (s, e) => _tray.RunElevatedFromControlPanel("startservice");
            btnRow.Controls.Add(_installBtn);
            btnRow.Controls.Add(_uninstallBtn);
            btnRow.Controls.Add(_startWatchdogBtn);
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
                _startWatchdogBtn.Visible = false;
            }
            else if (svc.IsRunning)
            {
                _serviceBullet.SetColor(OkColor);
                _serviceText.Text = "Running";
                _serviceStatusLabel.Text = "Status: Running";
                _installBtn.Enabled = false;
                _uninstallBtn.Enabled = true;
                _startWatchdogBtn.Visible = false;
            }
            else
            {
                _serviceBullet.SetColor(WarnColor);
                _serviceText.Text = "Installed, stopped";
                _serviceStatusLabel.Text = "Status: Installed, stopped";
                _installBtn.Enabled = false;
                _uninstallBtn.Enabled = true;
                _startWatchdogBtn.Visible = true;
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
}
