using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ZapretLauncher
{
    // --------------------------------------------------------- tg ws proxy
    /// <summary>
    /// TG WS Proxy (github.com/Flowseal/tg-ws-proxy) is a local MTProto proxy that carries Telegram's
    /// traffic over WebSocket. Its Windows build is a tray application with no command line, so the
    /// launcher runs it as a child process in its "portable" mode: a TgWsProxy_data folder next to the
    /// exe holds the config, and the launcher writes that config before every start. The launcher's own
    /// tray icon stands for the proxy too, so the proxy is kept from showing one (see ProxyRunner).
    /// </summary>
    public static class TgProxy
    {
        const string Api = "https://api.github.com/repos/Flowseal/tg-ws-proxy/releases/latest";
        public const string ProcessName = "TgWsProxy";

        public static string Dir { get { return Path.Combine(Paths.Root, "tgws"); } }
        public static string Exe { get { return Path.Combine(Dir, ProcessName + ".exe"); } }
        public static string DataDir { get { return Path.Combine(Dir, "TgWsProxy_data"); } }
        public static string ConfigFile { get { return Path.Combine(DataDir, "config.json"); } }
        public static string LogFile { get { return Path.Combine(DataDir, "proxy.log"); } }
        static string VersionFile { get { return Path.Combine(Dir, "version.txt"); } }

        public static bool IsInstalled { get { return File.Exists(Exe); } }

        public static string InstalledVersion
        {
            get
            {
                try { return IsInstalled && File.Exists(VersionFile) ? File.ReadAllText(VersionFile).Trim() : null; }
                catch { return null; }
            }
        }

        // the release has one exe per kind of Windows
        static string AssetName()
        {
            if (RuntimeInformation.OSArchitecture == Architecture.Arm64) return "TgWsProxy_windows_arm64.exe";
            if (!Environment.Is64BitOperatingSystem) return "TgWsProxy_windows_7_32bit.exe";
            return "TgWsProxy_windows.exe";
        }

        public static async Task<ReleaseInfo> GetLatestAsync()
        {
            using (var c = Web.Client(30))
            {
                string json = await c.GetStringAsync(Api).ConfigureAwait(false);
                var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                var url = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+/" + Regex.Escape(AssetName()) + ")\"");
                if (!tag.Success || !url.Success)
                    throw new Exception("На GitHub не найдена сборка TG WS Proxy для этой Windows");
                return new ReleaseInfo { Tag = tag.Groups[1].Value, Url = url.Groups[1].Value };
            }
        }

        public static async Task InstallAsync(ReleaseInfo rel, IProgress<double> progress)
        {
            Directory.CreateDirectory(Dir);
            string tmp = Exe + ".download";
            try
            {
                await Web.DownloadAsync(rel.Url, tmp, progress).ConfigureAwait(false);

                // the proxy is a ~20 MB one-file build; anything tiny is an error page or a file an antivirus cut short
                if (!File.Exists(tmp) || new FileInfo(tmp).Length < 1024 * 1024)
                    throw new Exception("файл скачался не полностью — возможно, его удалил антивирус");

                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(Exe)) File.Delete(Exe);
                        File.Move(tmp, Exe);
                        break;
                    }
                    catch (Exception)
                    {
                        if (attempt >= 3) throw new Exception("не удалось заменить старый файл — он занят другой программой");
                        await Task.Delay(700).ConfigureAwait(false);
                    }
                }
                File.WriteAllText(VersionFile, rel.Tag);
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }

        /// <summary>The last thing the proxy wrote to its log - the reason when it would not start.</summary>
        public static string LastLogLine()
        {
            try
            {
                if (!File.Exists(LogFile)) return null;
                using (var fs = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(fs, Encoding.UTF8))
                {
                    string last = null, line;
                    while ((line = reader.ReadLine()) != null)
                        if (line.Trim().Length > 0) last = line.Trim();
                    if (last == null) return null;
                    // "2026-01-01 12:00:00,000  ERROR  name  message" -> "message"
                    var m = Regex.Match(last, @"^\S+ \S+\s+\w+\s+\S+\s+(.*)$");
                    last = m.Success ? m.Groups[1].Value : last;
                    return last.Length > 200 ? last.Substring(0, 200) + "…" : last;
                }
            }
            catch { return null; }
        }
    }

    // -------------------------------------------------------- proxy config
    /// <summary>
    /// The proxy's own config.json. Only a few keys are shown in the launcher; the rest are kept as they
    /// are, so anything changed in the proxy's own settings window survives.
    /// </summary>
    public sealed class ProxyConfig
    {
        Dictionary<string, object> _all;

        static Dictionary<string, object> Defaults()
        {
            return new Dictionary<string, object>
            {
                { "host", "127.0.0.1" },
                { "port", 1443 },
                { "secret", NewSecret() },
                { "dc_ip", new ArrayList { "2:149.154.167.220", "4:149.154.167.220" } },
                { "verbose", false },
                { "check_updates", false },
                { "log_max_mb", 5 },
                { "buf_kb", 256 },
                { "pool_size", 4 },
                { "cfproxy", true },
                { "h2", true },
                { "cfproxy_user_domain_enabled", false },
                { "cfproxy_user_domain", new ArrayList() },
                { "cfproxy_worker_enabled", false },
                { "cfproxy_worker_domain", new ArrayList() },
                { "force_test_dc", false },
                { "no_secure", false },
                { "appearance", "auto" },
                { "language", "ru" },
                { "autostart", false },
            };
        }

        public static string NewSecret()
        {
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return string.Concat(bytes.Select(b => b.ToString("x2")));
        }

        static bool IsSecret(string s) { return s != null && Regex.IsMatch(s, "^[0-9a-fA-F]{32}$"); }

        public static ProxyConfig Load()
        {
            var cfg = new ProxyConfig { _all = Defaults() };
            try
            {
                if (File.Exists(TgProxy.ConfigFile))
                {
                    var read = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
                        File.ReadAllText(TgProxy.ConfigFile, Encoding.UTF8));
                    if (read != null)
                        foreach (var kv in read) cfg._all[kv.Key] = kv.Value;
                }
            }
            catch { }
            if (!IsSecret(cfg.Secret)) cfg.Secret = NewSecret();
            return cfg;
        }

        public void Save()
        {
            // the launcher starts and updates the proxy itself
            _all["check_updates"] = false;
            _all["autostart"] = false;

            Directory.CreateDirectory(TgProxy.DataDir);
            File.WriteAllText(TgProxy.ConfigFile, new JavaScriptSerializer().Serialize(_all), new UTF8Encoding(false));

            // These markers tell the proxy its one-off windows have already been shown: the connection guide
            // (the launcher has its own "connect" button) and the IPv6 notice (repeated in that button's hint).
            foreach (var marker in new[] { ".first_run_done_mtproto", ".ipv6_warned" })
            {
                string path = Path.Combine(TgProxy.DataDir, marker);
                if (!File.Exists(path)) File.WriteAllText(path, "");
            }
        }

        string Text(string key, string fallback)
        {
            object v;
            return _all.TryGetValue(key, out v) && v is string && ((string)v).Length > 0 ? (string)v : fallback;
        }

        bool Flag(string key, bool fallback)
        {
            object v;
            return _all.TryGetValue(key, out v) && v is bool ? (bool)v : fallback;
        }

        public string Host { get { return Text("host", "127.0.0.1"); } set { _all["host"] = value; } }

        public int Port
        {
            get
            {
                object v;
                int port;
                if (_all.TryGetValue("port", out v) && v != null && int.TryParse(v.ToString(), out port) && port > 0 && port < 65536) return port;
                return 1443;
            }
            set { _all["port"] = value; }
        }

        public string Secret { get { return Text("secret", null); } set { _all["secret"] = value; } }

        /// <summary>"DC:IP" pairs: which address to use for which Telegram data centre.</summary>
        public List<string> DcIp
        {
            get
            {
                object v;
                var list = new List<string>();
                if (_all.TryGetValue("dc_ip", out v) && v is IEnumerable && !(v is string))
                    foreach (var item in (IEnumerable)v)
                        if (item != null) list.Add(item.ToString());
                return list;
            }
            set { _all["dc_ip"] = new ArrayList(value); }
        }

        public bool CfProxy { get { return Flag("cfproxy", true); } set { _all["cfproxy"] = value; } }
        public bool H2 { get { return Flag("h2", true); } set { _all["h2"] = value; } }

        /// <summary>The address Telegram on this computer connects to.</summary>
        public string LinkHost { get { return Host == "0.0.0.0" || Host == "::" ? "127.0.0.1" : Host; } }

        /// <summary>Opening this link makes Telegram offer to add the proxy.</summary>
        public string Link { get { return "tg://proxy?server=" + LinkHost + "&port=" + Port + "&secret=dd" + Secret; } }
    }

    // -------------------------------------------------------- proxy runner
    /// <summary>
    /// Starts and stops the proxy. Normally it is started on a desktop of its own, one that is never shown:
    /// a tray icon can only be added on the desktop the taskbar lives on, so there the proxy runs without
    /// one (its tray library ignores the failure) and nothing of it shows up beside the launcher's icon.
    /// </summary>
    public sealed class ProxyRunner
    {
        const string DesktopName = "ZapretLauncherProxy";
        const uint GenericAll = 0x10000000;

        static IntPtr _desktop;   // kept open for as long as the launcher lives
        Process _p;
        bool _expected;

        public event Action Crashed;

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumProc callback, IntPtr param);
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
        delegate bool EnumProc(IntPtr hwnd, IntPtr param);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFO
        {
            public int cb;
            public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateProcess(string app, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
            bool inheritHandles, uint flags, IntPtr environment, string directory, ref STARTUPINFO startup, out PROCESS_INFORMATION info);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr devmode, uint flags, uint access, IntPtr security);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr OpenDesktop(string name, uint flags, bool inherit, uint access);
        [DllImport("user32.dll")]
        static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll")]
        static extern bool SetThreadDesktop(IntPtr desktop);

        public bool IsRunning
        {
            get
            {
                try { return _p != null && !_p.HasExited; }
                catch { return false; }
            }
        }

        // every process running the launcher's copy of the proxy (a one-file build is two: unpacker and program)
        static List<Process> Ours()
        {
            var list = new List<Process>();
            string exe;
            try { exe = Path.GetFullPath(TgProxy.Exe); }
            catch { return list; }
            foreach (var p in Process.GetProcessesByName(TgProxy.ProcessName))
            {
                try
                {
                    if (string.Equals(Path.GetFullPath(p.MainModule.FileName), exe, StringComparison.OrdinalIgnoreCase)) list.Add(p);
                }
                catch { }
            }
            return list;
        }

        /// <summary>
        /// True when a TG WS Proxy other than the launcher's own is already running: it allows a single copy
        /// and would only show its "already running" box.
        /// </summary>
        public static bool ForeignRunning
        {
            get
            {
                if (Ours().Count > 0) return false;
                try
                {
                    Mutex m;
                    if (!Mutex.TryOpenExisting(@"Local\TgWsProxy_SingleInstance", out m)) return false;
                    m.Dispose();
                    return true;
                }
                catch (UnauthorizedAccessException) { return true; }
                catch { return false; }
            }
        }

        /// <summary>
        /// hidden: on the desktop of its own, without a tray icon. If that desktop cannot be had, or when
        /// asked not to, the proxy runs in the open like any program, tray icon included.
        /// </summary>
        public void Start(bool hidden)
        {
            Stop();
            _expected = false;

            Process p = null;
            if (hidden)
            {
                try { p = StartOutOfSight(); }
                catch { }
            }
            if (p == null)
            {
                p = Process.Start(new ProcessStartInfo(TgProxy.Exe, "--portable")
                {
                    WorkingDirectory = TgProxy.Dir,
                    UseShellExecute = false
                });
            }

            _p = p;
            p.Exited += (o, e) =>
            {
                if (!_expected && ReferenceEquals(o, _p))
                {
                    var h = Crashed;
                    if (h != null) h();
                }
            };
            try { p.EnableRaisingEvents = true; }
            catch { }   // it is already gone; whoever started it finds that out from IsRunning
        }

        static Process StartOutOfSight()
        {
            if (_desktop == IntPtr.Zero) _desktop = CreateDesktop(DesktopName, IntPtr.Zero, IntPtr.Zero, 0, GenericAll, IntPtr.Zero);
            if (_desktop == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();

            var startup = new STARTUPINFO { lpDesktop = DesktopName };
            startup.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            PROCESS_INFORMATION info;
            var commandLine = new StringBuilder("\"" + TgProxy.Exe + "\" --portable");
            if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, TgProxy.Dir, ref startup, out info))
                throw new System.ComponentModel.Win32Exception();

            CloseHandle(info.hThread);
            try { return Process.GetProcessById(info.dwProcessId); }
            finally { CloseHandle(info.hProcess); }
        }

        // Asks the proxy's tray window to quit: its tray library (pystray) leaves its message loop on
        // WM_USER + 10. Reaches the windows of the desktop the calling thread is on.
        static bool AskToQuit(HashSet<uint> pids)
        {
            bool asked = false;
            try
            {
                EnumWindows((hwnd, param) =>
                {
                    uint pid;
                    GetWindowThreadProcessId(hwnd, out pid);
                    if (pids.Contains(pid))
                    {
                        var name = new StringBuilder(256);
                        GetClassName(hwnd, name, name.Capacity);
                        if (name.ToString().EndsWith("SystemTrayIcon", StringComparison.Ordinal))
                            asked |= PostMessage(hwnd, 0x0400 + 10, IntPtr.Zero, IntPtr.Zero);
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return asked;
        }

        public void Stop()
        {
            _expected = true;
            var ours = Ours();
            if (ours.Count > 0)
            {
                // Ask nicely first, so the proxy closes its connections and clears the folder it unpacked
                // itself into. Anything still alive after that is killed.
                var pids = new HashSet<uint>(ours.Select(p => (uint)p.Id));
                bool asked = AskToQuit(pids);

                // a window on another desktop only takes messages from a thread that has moved there
                IntPtr desktop = OpenDesktop(DesktopName, 0, false, GenericAll);
                if (desktop != IntPtr.Zero)
                {
                    var visitor = new Thread(() => { if (SetThreadDesktop(desktop)) asked |= AskToQuit(pids); }) { IsBackground = true };
                    visitor.Start();
                    visitor.Join(3000);
                    CloseDesktop(desktop);
                }

                // on its way out the proxy gives its own connections up to five seconds
                var until = DateTime.UtcNow.AddMilliseconds(asked ? 7000 : 1000);
                while (DateTime.UtcNow < until && ours.Any(Alive)) Thread.Sleep(100);
                foreach (var p in ours)
                {
                    try { if (Alive(p)) { p.Kill(); p.WaitForExit(2000); } } catch { }
                }
            }
            _p = null;
        }

        static bool Alive(Process p)
        {
            try { return !p.HasExited; }
            catch { return false; }
        }

        /// <summary>Whether the port can still be taken, i.e. nothing else is listening on it.</summary>
        public static bool PortFree(string host, int port)
        {
            IPAddress address;
            if (!IPAddress.TryParse(host, out address)) address = IPAddress.Loopback;
            try
            {
                var probe = new TcpListener(address, port);
                probe.Start();
                probe.Stop();
                return true;
            }
            catch (SocketException) { return false; }
        }

        public static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
            finally { probe.Stop(); }
        }

        /// <summary>Whether something accepts connections on the port - i.e. the proxy is up.</summary>
        public static async Task<bool> PortOpenAsync(string host, int port, int timeoutMs)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var connect = client.ConnectAsync(host, port);
                    if (await Task.WhenAny(connect, Task.Delay(timeoutMs)).ConfigureAwait(false) != connect) return false;
                    await connect.ConfigureAwait(false);
                    return client.Connected;
                }
            }
            catch { return false; }
        }
    }
}
