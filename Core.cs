using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Drawing = System.Drawing;

namespace ZapretLauncher
{
    // ---------------------------------------------------------------- paths
    public static class Paths
    {
        public static readonly string Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZapretLauncher");

        public static string Engine { get { return Path.Combine(Root, "engine"); } }
        public static string SettingsFile { get { return Path.Combine(Root, "settings.ini"); } }
        public static string VersionFile { get { return Path.Combine(Root, "engine.version"); } }
    }

    // ------------------------------------------------------------- settings
    public sealed class Settings
    {
        public string Strategy = "general";
        public bool GameFilter;
        public bool Autostart;
        public bool CloseToTray = true;
        public bool ProxyAuto;              // start TG WS Proxy together with the launcher
        public string Tab = "zapret";       // the tab that was open last
        public string ProxyLinked = "";     // the proxy address and key Telegram has already been offered
        public string Background = "";      // the picture behind everything; empty for the star sky
        public int Blur = 60;               // how much the glass blurs what is under it, 0..100
        public string Accent = "#FFFFFF";   // the accent colour
        public int WindowWidth, WindowHeight;   // the size the window was left at; 0 until it has been resized

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                foreach (var line in File.ReadAllLines(Paths.SettingsFile))
                {
                    int i = line.IndexOf('=');
                    if (i < 1) continue;
                    string k = line.Substring(0, i).Trim();
                    string v = line.Substring(i + 1).Trim();
                    switch (k)
                    {
                        case "strategy": s.Strategy = v; break;
                        case "gamefilter": s.GameFilter = v == "1"; break;
                        case "autostart": s.Autostart = v == "1"; break;
                        case "closetotray": s.CloseToTray = v == "1"; break;
                        case "proxyauto": s.ProxyAuto = v == "1"; break;
                        case "tab": s.Tab = v; break;
                        case "proxylinked": s.ProxyLinked = v; break;
                        case "background": s.Background = v; break;
                        case "blur":
                            int blur;
                            if (int.TryParse(v, out blur)) s.Blur = Math.Max(0, Math.Min(100, blur));
                            break;
                        case "accent": s.Accent = v; break;
                        case "width": int.TryParse(v, out s.WindowWidth); break;
                        case "height": int.TryParse(v, out s.WindowHeight); break;
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Paths.Root);
                File.WriteAllLines(Paths.SettingsFile, new[]
                {
                    "strategy=" + Strategy,
                    "gamefilter=" + (GameFilter ? "1" : "0"),
                    "autostart=" + (Autostart ? "1" : "0"),
                    "closetotray=" + (CloseToTray ? "1" : "0"),
                    "proxyauto=" + (ProxyAuto ? "1" : "0"),
                    "tab=" + Tab,
                    "proxylinked=" + ProxyLinked,
                    "background=" + Background,
                    "blur=" + Blur,
                    "accent=" + Accent,
                    "width=" + WindowWidth,
                    "height=" + WindowHeight,
                });
            }
            catch { }
        }
    }

    // ------------------------------------------------------------- helpers
    static class Sys
    {
        public static int Run(string file, string args, int timeoutMs = 10000)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return -1;
                    }
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }
    }

    static class Web
    {
        public static HttpClient Client(int timeoutSeconds = 300)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("ZapretLauncher/1.0");
            return c;
        }

        /// <summary>Downloads a file; progress runs from 0 to 1 when the server says how big the file is.</summary>
        public static async Task DownloadAsync(string url, string path, IProgress<double> progress)
        {
            using (var c = Client())
            using (var resp = await c.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? -1;
                using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var dst = File.Create(path))
                {
                    var buf = new byte[81920];
                    long read = 0;
                    int n;
                    while ((n = await src.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buf, 0, n).ConfigureAwait(false);
                        read += n;
                        if (total > 0 && progress != null) progress.Report((double)read / total);
                    }
                }
            }
        }
    }

    // -------------------------------------------------------------- engine
    public sealed class Strategy : System.ComponentModel.INotifyPropertyChanged
    {
        bool _works;

        public string Name { get; set; }
        public string BatPath { get; set; }

        /// <summary>Set by the auto-pick on every strategy that got the test sites through; the list marks such rows.</summary>
        public bool Works
        {
            get { return _works; }
            set
            {
                if (_works == value) return;
                _works = value;
                var h = PropertyChanged;
                if (h != null) h(this, new System.ComponentModel.PropertyChangedEventArgs("Works"));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        public override string ToString() { return Name; }
    }

    public sealed class ReleaseInfo
    {
        public string Tag;
        public string Url;
    }

    public static class Engine
    {
        public static string FindRoot()
        {
            return FindRootIn(Paths.Engine);
        }

        public static string FindRootIn(string dir)
        {
            if (!Directory.Exists(dir)) return null;
            if (File.Exists(Path.Combine(dir, "bin", "winws.exe"))) return dir;
            foreach (var sub in Directory.GetDirectories(dir))
                if (File.Exists(Path.Combine(sub, "bin", "winws.exe"))) return sub;
            return null;
        }

        public static bool IsInstalled { get { return FindRoot() != null; } }

        public static string InstalledVersion
        {
            get
            {
                try { return File.Exists(Paths.VersionFile) ? File.ReadAllText(Paths.VersionFile).Trim() : null; }
                catch { return null; }
            }
        }

        static string NaturalKey(string name)
        {
            return Regex.Replace(name, @"\d+", m => m.Value.PadLeft(5, '0')).ToLowerInvariant();
        }

        public static List<Strategy> ListStrategies()
        {
            var root = FindRoot();
            if (root == null) return new List<Strategy>();
            return Directory.GetFiles(root, "*.bat")
                .Where(f => !Path.GetFileName(f).StartsWith("service", StringComparison.OrdinalIgnoreCase))
                .Select(f => new Strategy { Name = Path.GetFileNameWithoutExtension(f), BatPath = f })
                .OrderBy(s => NaturalKey(s.Name))
                .ToList();
        }

        /// <summary>
        /// Creates the user list files with the same placeholders service.bat writes
        /// (winws treats an empty list as "match everything"). Returns the lists folder.
        /// </summary>
        public static string EnsureUserLists()
        {
            var root = FindRoot();
            if (root == null) return null;
            string lists = Path.Combine(root, "lists");
            Directory.CreateDirectory(lists);

            var defaults = new Dictionary<string, string>
            {
                { "list-general-user.txt", "# Never leave this file empty\r\ndomain.example.abc\r\n" },
                { "list-exclude-user.txt", "domain.example.abc\r\n" },
                { "ipset-exclude-user.txt", "203.0.113.113/32\r\n" },
            };
            foreach (var d in defaults)
            {
                string path = Path.Combine(lists, d.Key);
                if (!File.Exists(path)) File.WriteAllText(path, d.Value, new UTF8Encoding(false));
            }
            return lists;
        }

        static string Rep(string input, string key, string value)
        {
            return Regex.Replace(input, Regex.Escape(key), m => value, RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Reads a strategy .bat file from the zapret bundle, extracts the winws.exe command line
        /// (joining lines that end with ^) and substitutes the batch variables.
        /// This lets us run winws.exe directly, with no console window.
        /// </summary>
        public static string BuildArgs(Strategy s, bool gameFilter)
        {
            string root = Path.GetDirectoryName(s.BatPath);
            var sb = new StringBuilder();
            bool collecting = false;

            foreach (var raw in File.ReadAllLines(s.BatPath, Encoding.UTF8))
            {
                string line = raw.TrimEnd();
                if (!collecting)
                {
                    string t = line.TrimStart();
                    if (t.StartsWith("::") || t.StartsWith("rem ", StringComparison.OrdinalIgnoreCase)) continue;
                    int i = line.IndexOf("winws.exe", StringComparison.OrdinalIgnoreCase);
                    if (i < 0) continue;
                    string lower = t.ToLowerInvariant();
                    if (lower.StartsWith("taskkill") || lower.StartsWith("tasklist") || lower.StartsWith("echo")) continue;

                    line = line.Substring(i + "winws.exe".Length).TrimStart();
                    if (line.StartsWith("\"")) line = line.Substring(1);
                    collecting = true;
                }

                bool continues = line.EndsWith("^");
                if (continues) line = line.Substring(0, line.Length - 1);
                sb.Append(' ').Append(line.Trim());
                if (!continues) break;
            }

            if (sb.Length == 0)
                throw new InvalidOperationException("Не удалось разобрать файл стратегии " + Path.GetFileName(s.BatPath));

            string bin = Path.Combine(root, "bin") + "\\";
            string lists = Path.Combine(root, "lists") + "\\";
            string gf = gameFilter ? "1024-65535" : "12";

            string args = sb.ToString().Trim();
            args = Rep(args, "%BIN%", bin);
            args = Rep(args, "%LISTS%", lists);
            args = Rep(args, "%~dp0", root + "\\");
            args = Rep(args, "%GameFilterTCP%", gf);
            args = Rep(args, "%GameFilterUDP%", gf);
            args = Rep(args, "%GameFilter%", gf);
            return args;
        }
    }

    // ------------------------------------------------------------- updater
    public static class Updater
    {
        const string Api = "https://api.github.com/repos/Flowseal/zapret-discord-youtube/releases/latest";

        public static async Task<ReleaseInfo> GetLatestAsync()
        {
            using (var c = Web.Client(30))
            {
                string json = await c.GetStringAsync(Api).ConfigureAwait(false);
                var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                var url = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+\\.zip)\"");
                if (!tag.Success || !url.Success)
                    throw new Exception("На GitHub не найден архив релиза");
                return new ReleaseInfo { Tag = tag.Groups[1].Value, Url = url.Groups[1].Value };
            }
        }

        public static async Task InstallAsync(ReleaseInfo rel, IProgress<double> progress)
        {
            Directory.CreateDirectory(Paths.Root);
            string tmp = Path.Combine(Paths.Root, "tmp_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string zip = tmp + ".zip";
            Directory.CreateDirectory(tmp);

            try
            {
                using (var c = Web.Client())
                using (var resp = await c.GetAsync(rel.Url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? -1;
                    using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var dst = File.Create(zip))
                    {
                        var buf = new byte[81920];
                        long read = 0;
                        int n;
                        while ((n = await src.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
                        {
                            await dst.WriteAsync(buf, 0, n).ConfigureAwait(false);
                            read += n;
                            if (total > 0 && progress != null) progress.Report((double)read / total);
                        }
                    }
                }

                ZipFile.ExtractToDirectory(zip, tmp);

                string newRoot = Engine.FindRootIn(tmp);
                if (newRoot == null)
                    throw new Exception("В архиве нет bin\\winws.exe — возможно, антивирус удалил файл");

                // Keep the user's own list files (e.g. list-general-user.txt) across updates.
                string oldLists = Path.Combine(Paths.Engine, "lists");
                string newLists = Path.Combine(newRoot, "lists");
                if (Directory.Exists(oldLists) && Directory.Exists(newLists))
                {
                    foreach (var f in Directory.GetFiles(oldLists, "*user*.txt"))
                        File.Copy(f, Path.Combine(newLists, Path.GetFileName(f)), true);
                }

                if (Directory.Exists(Paths.Engine))
                {
                    for (int attempt = 0; ; attempt++)
                    {
                        try { Directory.Delete(Paths.Engine, true); break; }
                        catch (Exception)
                        {
                            if (attempt >= 3) throw new Exception("Не удалось заменить старые файлы — они заняты другой программой");
                            await Task.Delay(700).ConfigureAwait(false);
                        }
                    }
                }

                Directory.Move(newRoot, Paths.Engine);
                File.WriteAllText(Paths.VersionFile, rel.Tag);
            }
            finally
            {
                try { File.Delete(zip); } catch { }
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            }
        }
    }

    // -------------------------------------------------------------- runner
    public sealed class Runner
    {
        Process _p;
        bool _expected;
        readonly StringBuilder _out = new StringBuilder();

        public event Action Crashed;

        public bool IsRunning
        {
            get
            {
                try { return _p != null && !_p.HasExited; }
                catch { return false; }
            }
        }

        public string LastOutput { get { lock (_out) return _out.ToString().Trim(); } }

        void Append(string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            lock (_out)
            {
                if (_out.Length < 4000) _out.AppendLine(s);
            }
        }

        public void Start(Strategy s, bool gameFilter)
        {
            Stop();

            string root = Engine.FindRoot();
            if (root == null) throw new InvalidOperationException("Компоненты zapret не установлены");
            string exe = Path.Combine(root, "bin", "winws.exe");
            if (!File.Exists(exe))
                throw new FileNotFoundException("winws.exe не найден — возможно, его удалил антивирус");

            // The strategies point winws at the *-user.txt lists, and winws refuses to start if one is
            // missing. The original .bat files create them through service.bat; we have to do the same.
            Engine.EnsureUserLists();

            string args = Engine.BuildArgs(s, gameFilter);

            // zapret needs TCP timestamps enabled (same step as the official service.bat).
            Sys.Run("netsh", "interface tcp set global timestamps=enabled", 5000);

            lock (_out) _out.Clear();
            _expected = false;

            var psi = new ProcessStartInfo(exe, args)
            {
                WorkingDirectory = Path.GetDirectoryName(exe),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (o, e) => Append(e.Data);
            p.ErrorDataReceived += (o, e) => Append(e.Data);
            p.Exited += (o, e) =>
            {
                if (!_expected && ReferenceEquals(o, _p))
                {
                    var h = Crashed;
                    if (h != null) h();
                }
            };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _p = p;
        }

        public void Stop()
        {
            _expected = true;
            bool had = false;

            try
            {
                if (_p != null && !_p.HasExited) { had = true; _p.Kill(); _p.WaitForExit(2000); }
            }
            catch { }

            foreach (var pr in Process.GetProcessesByName("winws"))
            {
                had = true;
                try { pr.Kill(); pr.WaitForExit(2000); } catch { }
            }

            _p = null;

            // Unload the WinDivert driver so files can be replaced and nothing is left behind.
            if (had)
            {
                Sys.Run("sc", "stop WinDivert", 4000);
                Sys.Run("sc", "delete WinDivert", 4000);
            }
        }
    }

    // ----------------------------------------------------------- autostart
    public static class Autostart
    {
        const string TaskName = "ZapretLauncher";

        /// <summary>
        /// Creates (or removes) a scheduled task that starts the launcher at logon with highest
        /// privileges, so Windows does not show a UAC prompt on every boot.
        /// </summary>
        public static void Set(bool on, string exe = null)
        {
            if (!on)
            {
                Sys.Run("schtasks", "/Delete /TN \"" + TaskName + "\" /F");
                return;
            }

            if (exe == null) exe = Process.GetCurrentProcess().MainModule.FileName;
            string user = WindowsIdentity.GetCurrent().Name;

            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + SecurityElement.Escape(user) + "</UserId><Delay>PT5S</Delay></LogonTrigger></Triggers>\r\n" +
                "  <Principals><Principal id=\"Author\"><UserId>" + SecurityElement.Escape(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
                "  <Settings>\r\n" +
                "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
                "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
                "    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
                "    <Enabled>true</Enabled>\r\n" +
                "  </Settings>\r\n" +
                "  <Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(exe) + "</Command><Arguments>--autostart</Arguments></Exec></Actions>\r\n" +
                "</Task>\r\n";

            string tmp = Path.Combine(Path.GetTempPath(), "zapretlauncher_task.xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            int code = Sys.Run("schtasks", "/Create /TN \"" + TaskName + "\" /XML \"" + tmp + "\" /F");
            try { File.Delete(tmp); } catch { }
            if (code != 0) throw new Exception("не удалось создать задачу планировщика (код " + code + ")");
        }
    }

    // --------------------------------------------------------------- probe
    /// <summary>Quick connectivity check used by "Автоподбор": YouTube and Discord must answer.</summary>
    public static class Probe
    {
        static readonly string[] Urls =
        {
            "https://www.youtube.com/",
            "https://discord.com/",
            "https://i.ytimg.com/"
        };

        public static async Task<bool> OkAsync(CancellationToken ct)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var handler = new HttpClientHandler { UseProxy = false };
            using (var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(7) })
            {
                c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
                var results = await Task.WhenAll(Urls.Select(u => One(c, u, ct)));
                return results.All(r => r);
            }
        }

        static async Task<bool> One(HttpClient c, string url, CancellationToken ct)
        {
            try
            {
                using (await c.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)) { }
                return true; // any HTTP answer means the TLS handshake got through
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return false; }
        }
    }

    // --------------------------------------------------------------- icons
    /// <summary>
    /// Tray icons: the application icon while the bypass is on, a greyed copy of it while it is off.
    /// </summary>
    public static class IconFactory
    {
        [DllImport("user32.dll")]
        static extern IntPtr LoadImage(IntPtr instance, IntPtr name, uint type, int cx, int cy, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);

        public static Drawing.Icon Tray(bool on)
        {
            var size = System.Windows.Forms.SystemInformation.SmallIconSize;
            if (!on)
            {
                using (var s = typeof(IconFactory).Assembly.GetManifestResourceStream("app-off.ico"))
                    return new Drawing.Icon(s, size);
            }

            // "On" is the exe's own icon (app.ico); the compiler stores it under resource id 32512.
            IntPtr h = LoadImage(GetModuleHandle(null), (IntPtr)32512, 1 /* IMAGE_ICON */, size.Width, size.Height, 0);
            if (h == IntPtr.Zero) return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
            try { return (Drawing.Icon)Drawing.Icon.FromHandle(h).Clone(); }
            finally { DestroyIcon(h); }
        }
    }
}
