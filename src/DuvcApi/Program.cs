using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DuvcApi
{
    internal static class Program
    {
        private const string ServiceNameConst = "DuvcApi";
        private const string ServiceDisplayName = "DUVC API Service";
        private const string DefaultCameraName = "USB Camera";
        private const int DefaultPort = 3790;

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0)
            {
                var command = args[0].Trim().ToLowerInvariant();
                switch (command)
                {
                    case "install":
                        return ServiceInstaller.Install(ServiceNameConst, ServiceDisplayName);
                    case "uninstall":
                        return ServiceInstaller.Uninstall(ServiceNameConst);
                    case "service":
                        ServiceBaseHost.Run(ServiceNameConst, ServiceDisplayName);
                        return 0;
                    case "tray":
                        TrayApp.Run(false);
                        return 0;
                    case "run":
                        ConsoleHost.Run();
                        return 0;
                    case "app":
                        TrayApp.Run(true);
                        return 0;
                    default:
                        Console.Error.WriteLine("Unknown command. Use: install | uninstall | service | tray | run");
                        return 2;
                }
            }

            if (Environment.UserInteractive)
            {
                TrayApp.Run(true);
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
    }

    internal sealed class ConsoleHost
    {
        public static void Run()
        {
            try
            {
                var server = new ApiServer(Program.GetPort(), Program.GetCameraName());
                server.Start();
                Console.WriteLine("DUVC API running on http://127.0.0.1:{0}/", Program.GetPort());
                Console.WriteLine("Press Enter to stop.");
                Logger.Info("API started in console mode.");

                var trayThread = new Thread(() => TrayApp.Run(false))
                {
                    IsBackground = true,
                    Name = "DuvcApiTray"
                };
                trayThread.SetApartmentState(ApartmentState.STA);
                trayThread.Start();

                Console.ReadLine();
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

    internal sealed class ServiceBaseHost
    {
        public static void Run(string serviceName, string displayName)
        {
            ServiceBase.Run(new DuvcApiService(serviceName, displayName));
        }
    }

    internal sealed class DuvcApiService : ServiceBase
    {
        private readonly ApiServer _server;

        public DuvcApiService(string serviceName, string displayName)
        {
            ServiceName = serviceName;
            CanStop = true;
            CanPauseAndContinue = false;
            AutoLog = true;
            _server = new ApiServer(Program.GetPort(), Program.GetCameraName());
        }

        protected override void OnStart(string[] args)
        {
            _server.Start();
        }

        protected override void OnStop()
        {
            _server.Stop();
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

        public ApiServer(int port, string cameraName)
        {
            _port = port;
            _cameraName = cameraName;
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

                Logger.Info(string.Format(CultureInfo.InvariantCulture, "{0} {1}", request.HttpMethod, path));

                if (path.Equals("/health", StringComparison.OrdinalIgnoreCase) && request.HttpMethod == "GET")
                {
                    HandleHealth(response);
                    return;
                }

                if (path.Equals("/api/cameras", StringComparison.OrdinalIgnoreCase) && request.HttpMethod == "GET")
                {
                    HandleListCameras(response);
                    return;
                }

                if (path.Equals("/api/usb-camera/set", StringComparison.OrdinalIgnoreCase) && request.HttpMethod == "POST")
                {
                    HandleSetByName(response, ReadJsonBody<SetRequest>(request));
                    return;
                }

                if (path.Equals("/api/usb-camera/get", StringComparison.OrdinalIgnoreCase) && request.HttpMethod == "POST")
                {
                    HandleGetByName(response, ReadJsonBody<GetRequest>(request));
                    return;
                }

                if (path.Equals("/api/usb-camera/reset", StringComparison.OrdinalIgnoreCase) && request.HttpMethod == "POST")
                {
                    HandleResetByName(response, ReadJsonBody<ResetRequest>(request));
                    return;
                }

                if (path.Equals("/api/usb-camera/capabilities", StringComparison.OrdinalIgnoreCase) && request.HttpMethod == "GET")
                {
                    HandleCapabilitiesByName(response);
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
                            HandleSet(response, index, ReadJsonBody<SetRequest>(request));
                            return;
                        case "get":
                            HandleGet(response, index, ReadJsonBody<GetRequest>(request));
                            return;
                        case "reset":
                            HandleReset(response, index, ReadJsonBody<ResetRequest>(request));
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
                    devices
                };
                WriteJson(response, cameraFound ? 200 : 503, payload);
            }
            catch (Exception ex)
            {
                WriteJson(response, 503, new { ok = false, error = ex.Message });
            }
        }

        private void HandleListCameras(HttpListenerResponse response)
        {
            var devices = DuvcCli.ListDevices();
            WriteJson(response, 200, new { ok = true, devices });
        }

        private void HandleSetByName(HttpListenerResponse response, SetRequest request)
        {
            var index = DuvcCli.FindDeviceIndex(DuvcCli.ListDevices(), _cameraName);
            if (!index.HasValue)
            {
                WriteJson(response, 404, new { ok = false, error = string.Format(CultureInfo.InvariantCulture, "Camera '{0}' not found.", _cameraName) });
                return;
            }
            HandleSet(response, index.Value, request);
        }

        private void HandleGetByName(HttpListenerResponse response, GetRequest request)
        {
            var index = DuvcCli.FindDeviceIndex(DuvcCli.ListDevices(), _cameraName);
            if (!index.HasValue)
            {
                WriteJson(response, 404, new { ok = false, error = string.Format(CultureInfo.InvariantCulture, "Camera '{0}' not found.", _cameraName) });
                return;
            }
            HandleGet(response, index.Value, request);
        }

        private void HandleResetByName(HttpListenerResponse response, ResetRequest request)
        {
            var index = DuvcCli.FindDeviceIndex(DuvcCli.ListDevices(), _cameraName);
            if (!index.HasValue)
            {
                WriteJson(response, 404, new { ok = false, error = string.Format(CultureInfo.InvariantCulture, "Camera '{0}' not found.", _cameraName) });
                return;
            }
            HandleReset(response, index.Value, request);
        }

        private void HandleCapabilitiesByName(HttpListenerResponse response)
        {
            var index = DuvcCli.FindDeviceIndex(DuvcCli.ListDevices(), _cameraName);
            if (!index.HasValue)
            {
                WriteJson(response, 404, new { ok = false, error = string.Format(CultureInfo.InvariantCulture, "Camera '{0}' not found.", _cameraName) });
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

            var commandArgs = DuvcCli.BuildSetArguments(index, request);
            var result = DuvcCli.Run(commandArgs);

            WriteJson(response, result.IsOk ? 200 : 500, new
            {
                ok = result.IsOk,
                output = result.StdOut,
                error = result.StdErr,
                exitCode = result.ExitCode
            });
        }

        private void HandleGet(HttpListenerResponse response, int index, GetRequest request)
        {
            if (request == null)
            {
                WriteJson(response, 400, new { ok = false, error = "Missing request body." });
                return;
            }

            var commandArgs = DuvcCli.BuildGetArguments(index, request);
            var result = DuvcCli.Run(commandArgs);

            WriteJson(response, result.ExitCode == 0 ? 200 : 500, new
            {
                ok = result.ExitCode == 0,
                output = result.StdOut,
                error = result.StdErr,
                exitCode = result.ExitCode
            });
        }

        private void HandleReset(HttpListenerResponse response, int index, ResetRequest request)
        {
            if (request == null)
            {
                WriteJson(response, 400, new { ok = false, error = "Missing request body." });
                return;
            }

            var commandArgs = DuvcCli.BuildResetArguments(index, request);
            var result = DuvcCli.Run(commandArgs);

            WriteJson(response, result.ExitCode == 0 ? 200 : 500, new
            {
                ok = result.ExitCode == 0,
                output = result.StdOut,
                error = result.StdErr,
                exitCode = result.ExitCode
            });
        }

        private void HandleCapabilities(HttpListenerResponse response, int index)
        {
            var result = DuvcCli.Run(string.Format(CultureInfo.InvariantCulture, "capabilities {0}", index));
            WriteJson(response, result.ExitCode == 0 ? 200 : 500, new
            {
                ok = result.ExitCode == 0,
                output = result.StdOut,
                error = result.StdErr,
                exitCode = result.ExitCode
            });
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

        private T ReadJsonBody<T>(HttpListenerRequest request) where T : class
        {
            if (!request.HasEntityBody)
            {
                return null;
            }

            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
            {
                var body = reader.ReadToEnd();
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
    }

    internal static class CorsHelper
    {
        public static string GetAllowedOrigin(string origin)
        {
            var configured = Environment.GetEnvironmentVariable("DUVC_API_ALLOWED_ORIGINS");
            if (string.IsNullOrWhiteSpace(configured))
            {
                return "*";
            }

            var parts = configured.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (string.Equals(trimmed, origin, StringComparison.OrdinalIgnoreCase))
                {
                    return origin;
                }
            }

            return string.Empty;
        }
    }

    internal sealed class SetRequest
    {
        public string domain { get; set; }
        public string property { get; set; }
        public string value { get; set; }
        public string mode { get; set; }
        public bool relative { get; set; }
    }

    internal sealed class GetRequest
    {
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

        public static List<CameraDevice> ListDevices()
        {
            var result = Run("list");
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
                builder.Append(request.value);
                if (!string.IsNullOrWhiteSpace(request.mode))
                {
                    builder.Append(' ').Append(request.mode);
                }
                return builder.ToString().Trim();
            }

            if (!string.IsNullOrWhiteSpace(request.mode))
            {
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

            return string.Format(CultureInfo.InvariantCulture, "reset {0} {1} {2}", index, request.domain, request.property);
        }

        public static DuvcCliResult Run(string arguments)
        {
            var exePath = ResolveExecutablePath();
            Logger.Info("duvc-cli " + arguments);
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

                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    Logger.Error(stderr.Trim());
                }
                if (!string.IsNullOrWhiteSpace(stdout))
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

            var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DuvcApi");
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

    internal static class ServiceInstaller
    {
        public static int Install(string serviceName, string displayName)
        {
            var exePath = Process.GetCurrentProcess().MainModule.FileName;
            var binPath = string.Format(CultureInfo.InvariantCulture, "\"{0}\" service", exePath);

            var createResult = RunSc(string.Format(CultureInfo.InvariantCulture, "create {0} binPath= \"{1}\" start= auto DisplayName= \"{2}\"", serviceName, binPath, displayName));
            if (createResult != 0)
            {
                return createResult;
            }

            RunSc(string.Format(CultureInfo.InvariantCulture, "description {0} \"Local API for DUVC camera control\"", serviceName));
            return RunSc(string.Format(CultureInfo.InvariantCulture, "start {0}", serviceName));
        }

        public static int Uninstall(string serviceName)
        {
            RunSc(string.Format(CultureInfo.InvariantCulture, "stop {0}", serviceName));
            return RunSc(string.Format(CultureInfo.InvariantCulture, "delete {0}", serviceName));
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
                process.WaitForExit(10000);
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();

                if (process.ExitCode != 0)
                {
                    Console.Error.WriteLine(stdout);
                    Console.Error.WriteLine(stderr);
                }

                return process.ExitCode;
            }
        }
    }

    internal sealed class TrayApp : ApplicationContext
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly System.Windows.Forms.Timer _timer;
        private Icon _okIcon;
        private Icon _badIcon;
        private string _lastTooltip;
        private LogForm _logForm;
        private readonly ApiServer _server;

        private TrayApp(bool startServer)
        {
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

            _okIcon = TrayIconFactory.CreateStatusIcon(Color.FromArgb(0, 200, 0));
            _badIcon = TrayIconFactory.CreateStatusIcon(Color.FromArgb(200, 0, 0));

            _notifyIcon = new NotifyIcon
            {
                Icon = _badIcon,
                Visible = true,
                Text = "DUVC API"
            };

            var menu = new ContextMenuStrip();
            var open = new ToolStripMenuItem("Open Health Page");
            open.Click += (sender, args) => OpenHealthPage();
            var log = new ToolStripMenuItem("Show Log");
            log.Click += (sender, args) => ShowLog();
            var exit = new ToolStripMenuItem("Exit");
            exit.Click += (sender, args) => ExitThread();
            menu.Items.Add(open);
            menu.Items.Add(log);
            menu.Items.Add(exit);
            _notifyIcon.ContextMenuStrip = menu;

            _timer = new System.Windows.Forms.Timer { Interval = 2000 };
            _timer.Tick += (sender, args) => UpdateStatus();
            _timer.Start();
            UpdateStatus();
        }

        public static void Run(bool startServer)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApp(startServer));
        }

        private void UpdateStatus()
        {
            var health = HealthClient.Check(Program.GetPort());
            if (health.ApiReachable && health.CameraFound)
            {
                SetIcon(_okIcon, string.Format(CultureInfo.InvariantCulture, "Camera '{0}' connected", health.CameraName));
            }
            else if (!health.ApiReachable)
            {
                SetIcon(_badIcon, "API not reachable");
            }
            else
            {
                SetIcon(_badIcon, string.Format(CultureInfo.InvariantCulture, "Camera '{0}' not found", health.CameraName));
            }
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

        private void OpenHealthPage()
        {
            var url = string.Format(CultureInfo.InvariantCulture, "http://127.0.0.1:{0}/health", Program.GetPort());
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }

        protected override void ExitThreadCore()
        {
            _timer.Stop();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            if (_okIcon != null)
            {
                _okIcon.Dispose();
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
            base.ExitThreadCore();
        }

        private void ShowLog()
        {
            if (_logForm == null || _logForm.IsDisposed)
            {
                _logForm = new LogForm();
                _logForm.Show();
            }
            else
            {
                _logForm.BringToFront();
                _logForm.Activate();
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

    internal static class Logger
    {
        private static readonly object Sync = new object();
        private static readonly Queue<string> Entries = new Queue<string>();
        private const int MaxEntries = 500;
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "DuvcApi",
            "duvc-api.log");

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

    internal sealed class LogForm : Form
    {
        private readonly TextBox _textBox;

        public LogForm()
        {
            Text = "DUVC API Log";
            Width = 720;
            Height = 420;

            _textBox = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                Dock = DockStyle.Fill,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9f),
                BackColor = Color.White
            };

            Controls.Add(_textBox);

            foreach (var line in Logger.Snapshot())
            {
                AppendLine(line);
            }

            Logger.Logged += OnLogged;
            FormClosed += OnClosed;
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
        }
    }
}
