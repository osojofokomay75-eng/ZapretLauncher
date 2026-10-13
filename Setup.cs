using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace ZapretLauncher
{
    /// <summary>
    /// The installer is the launcher itself: any copy that is not the installed one shows the setup wizard
    /// (--portable skips it), and --uninstall shows the removal wizard, both inside the main window.
    /// An installer never installs a stale version: if a newer one has been released, it downloads that
    /// version's installer and hands the job over to it.
    /// </summary>
    public enum SetupMode { None, Install, Uninstall }

    // ----------------------------------------------------------- installer
    public static class Installer
    {
        public const string AppName = "Zapret Launcher";
        const string ExeName = "ZapretLauncher.exe";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ZapretLauncher";

        public static string SelfPath { get { return Process.GetCurrentProcess().MainModule.FileName; } }

        public static string Version { get { return typeof(Installer).Assembly.GetName().Version.ToString(3); } }

        // --------------------------------------------- a newer installer
        const string Releases = "https://github.com/pikacu1337/ZapretLauncher/releases";
        const string SetupAsset = "ZapretLauncherSetup.exe";

        /// <summary>
        /// The latest release, if it is newer than this copy; otherwise null. It follows the releases page's
        /// "latest" address to the tag it stands for rather than asking the GitHub API, which answers one
        /// address only so many times an hour - and an installer that cannot ask installs an old version.
        /// </summary>
        public static async Task<ReleaseInfo> FindNewerAsync()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
            using (var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) })
            {
                c.DefaultRequestHeaders.UserAgent.ParseAdd("ZapretLauncher/" + Version);
                string url = Releases + "/latest";
                // one hop to the tag; one more in front of it if the repository has been renamed
                for (int hop = 0; hop < 5; hop++)
                {
                    using (var resp = await c.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                    {
                        var next = resp.Headers.Location;
                        if (next == null) return null;
                        url = (next.IsAbsoluteUri ? next : new Uri(new Uri(url), next)).ToString();
                    }
                    var m = Regex.Match(url, @"^(https://github\.com/[^/]+/[^/]+/releases)/tag/([^/?#]+)$");
                    if (!m.Success) continue;

                    string tag = Uri.UnescapeDataString(m.Groups[2].Value);
                    if (!IsNewer(tag)) return null;
                    return new ReleaseInfo { Tag = tag, Url = m.Groups[1].Value + "/download/" + m.Groups[2].Value + "/" + SetupAsset };
                }
                return null;
            }
        }

        /// <summary>Whether a release tag such as "v1.2" or "v1.2.1" stands for a later version than this copy.</summary>
        public static bool IsNewer(string tag)
        {
            string text = (tag ?? "").TrimStart('v', 'V');
            if (text.IndexOf('.') < 0) text += ".0";
            System.Version theirs;
            if (!System.Version.TryParse(text, out theirs)) return false;

            var ours = typeof(Installer).Assembly.GetName().Version;
            // "1.2" and "1.2.0" are the same release
            return new System.Version(theirs.Major, theirs.Minor, Math.Max(theirs.Build, 0))
                 > new System.Version(ours.Major, ours.Minor, Math.Max(ours.Build, 0));
        }

        /// <summary>Downloads that release's installer into %TEMP% and returns where it is.</summary>
        public static async Task<string> DownloadSetupAsync(ReleaseInfo release, IProgress<double> progress)
        {
            string name = new string(release.Tag.Where(ch => char.IsLetterOrDigit(ch) || ch == '.').ToArray());
            string path = Path.Combine(Path.GetTempPath(), "ZapretLauncherSetup-" + name + ".exe");
            try
            {
                await Web.DownloadAsync(release.Url, path, progress).ConfigureAwait(false);

                // an installer is a couple of hundred kilobytes; an error page or a file cut short is not one
                var head = new byte[2];
                using (var file = File.OpenRead(path))
                {
                    if (file.Length < 100 * 1024 || file.Read(head, 0, 2) != 2 || head[0] != 'M' || head[1] != 'Z')
                        throw new Exception("скачанный файл не похож на установщик");
                }
                return path;
            }
            catch
            {
                try { File.Delete(path); } catch { }
                throw;
            }
        }

        /// <summary>
        /// What the newer installer is started with: --continue tells it to go straight to installing with
        /// the choices already made here. An installer that does not know these simply shows its wizard.
        /// </summary>
        public static string HandOverArgs(string dir, bool desktop, bool autostart)
        {
            // backslashes right before the closing quote have to be doubled, or they swallow it ("E:\" -> "E:\\")
            string tail = new string('\\', dir.Length - dir.TrimEnd('\\').Length);
            return "--setup --continue --dir \"" + dir + tail + "\" --desktop " + (desktop ? "1" : "0")
                + " --autostart " + (autostart ? "1" : "0");
        }

        public static void HandOver(string setup, string dir, bool desktop, bool autostart)
        {
            // started from this, already elevated, process: no second UAC prompt
            Process.Start(new ProcessStartInfo(setup, HandOverArgs(dir, desktop, autostart)) { UseShellExecute = false });
        }

        /// <summary>The value that follows a switch on the command line, or null.</summary>
        public static string ArgAfter(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        public static string DefaultDir
        {
            get
            {
                // a 32-bit process is told "Program Files (x86)"; ProgramW6432 is the real one
                string root = Environment.GetEnvironmentVariable("ProgramW6432");
                if (string.IsNullOrEmpty(root)) root = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                return Path.Combine(root, AppName);
            }
        }

        static string StartMenuLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName + ".lnk"); }
        }

        static string DesktopLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"); }
        }

        static RegistryKey Machine()
        {
            return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        }

        /// <summary>Where a previous install lives, so a re-install offers the same folder.</summary>
        public static string ExistingDir()
        {
            try
            {
                using (var hklm = Machine())
                using (var key = hklm.OpenSubKey(UninstallKey))
                {
                    var dir = key == null ? null : key.GetValue("InstallLocation") as string;
                    return string.IsNullOrWhiteSpace(dir) ? null : dir;
                }
            }
            catch { return null; }
        }

        /// <summary>True when this exe is the copy the installer registered, not a stray download.</summary>
        public static bool IsInstalledCopy
        {
            get
            {
                string dir = ExistingDir();
                if (dir == null) return false;
                try
                {
                    return string.Equals(Path.GetFullPath(Path.Combine(dir, ExeName)), Path.GetFullPath(SelfPath),
                        StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }
        }

        /// <summary>Closes other launcher instances and winws, so their files can be replaced or removed.</summary>
        public static void StopRunning()
        {
            int me = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
            {
                if (p.Id == me) continue;
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            }
            new Runner().Stop();
            new ProxyRunner().Stop();
        }

        /// <summary>Copies the launcher, creates the shortcuts and registers the uninstaller. Returns the installed exe.</summary>
        public static string Install(string dir, bool desktopShortcut)
        {
            Directory.CreateDirectory(dir);
            string exe = Path.Combine(dir, ExeName);
            if (!string.Equals(Path.GetFullPath(SelfPath), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase))
                File.Copy(SelfPath, exe, true);

            Shortcut(StartMenuLink, exe);
            if (desktopShortcut) Shortcut(DesktopLink, exe);
            else TryDelete(DesktopLink);

            using (var hklm = Machine())
            using (var key = hklm.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", AppName);
                key.SetValue("DisplayVersion", Version);
                key.SetValue("DisplayIcon", exe);
                key.SetValue("InstallLocation", dir);
                key.SetValue("UninstallString", "\"" + exe + "\" --uninstall");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
            }
            return exe;
        }

        /// <summary>
        /// Removes everything Install created. With keepSettings the choices the user made (settings.ini,
        /// their own *user*.txt lists and the Telegram proxy's config) stay for a later re-install.
        /// </summary>
        public static void Uninstall(string dir, bool keepSettings)
        {
            StopRunning();
            try { Autostart.Set(false); } catch { }

            TryDelete(StartMenuLink);
            TryDelete(DesktopLink);
            try { using (var hklm = Machine()) hklm.DeleteSubKeyTree(UninstallKey, false); } catch { }

            RemoveData(keepSettings);

            // Only our own file: the folder was picked by the user and may hold other things.
            if (!string.IsNullOrEmpty(dir))
            {
                TryDelete(Path.Combine(dir, ExeName));
                try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
            }
        }

        static void RemoveData(bool keepSettings)
        {
            if (!Directory.Exists(Paths.Root)) return;
            if (!keepSettings)
            {
                try { Directory.Delete(Paths.Root, true); } catch { }
                return;
            }

            string lists = Path.Combine(Paths.Engine, "lists");
            foreach (var file in Directory.GetFiles(Paths.Root, "*", SearchOption.AllDirectories))
            {
                bool settings = string.Equals(file, Paths.SettingsFile, StringComparison.OrdinalIgnoreCase);
                bool userList = string.Equals(Path.GetDirectoryName(file), lists, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(file).IndexOf("user", StringComparison.OrdinalIgnoreCase) >= 0
                    && file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
                bool proxyConfig = string.Equals(file, TgProxy.ConfigFile, StringComparison.OrdinalIgnoreCase);
                if (!settings && !userList && !proxyConfig) TryDelete(file);
            }
            // deepest folders first, and only the ones left empty
            foreach (var sub in Directory.GetDirectories(Paths.Root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            {
                try { if (!Directory.EnumerateFileSystemEntries(sub).Any()) Directory.Delete(sub); } catch { }
            }
        }

        static void TryDelete(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
        }

        // .lnk files can only be written through the shell's COM object
        public static void Shortcut(string link, string target)
        {
            Type type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) throw new InvalidOperationException("WScript.Shell недоступен — ярлык не создан");
            object shell = Activator.CreateInstance(type);
            object lnk = null;
            try
            {
                lnk = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link });
                Type lt = lnk.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
                lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(target) });
                lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { target + ",0" });
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { AppName });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
            }
            finally
            {
                if (lnk != null) Marshal.FinalReleaseComObject(lnk);
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    // -------------------------------------------------------------- wizard
    public partial class MainWindow
    {
        enum SetupPage { Welcome, Options, Working, Done, Remove }

        SetupMode _setup;
        SetupPage _setupPage;
        string _setupDir, _installedExe;
        bool _setupBusy, _setupFailed, _selfDelete;
        bool _handedOver;   // an older installer started this one to finish its job: no looking for a still newer one

        void StartSetup(SetupMode mode, string dir)
        {
            _setup = mode;
            _setupDir = dir;

            Width = 760;
            Height = 540;
            MainView.Visibility = Visibility.Collapsed;
            SetupView.Visibility = Visibility.Visible;

            SetupCaption.Text = mode == SetupMode.Install ? " · установка" : " · удаление";
            SetupVersion.Text = "Версия " + Installer.Version;
            SetupPath.Text = Installer.ExistingDir() ?? Installer.DefaultDir;
            GoTo(mode == SetupMode.Install ? SetupPage.Welcome : SetupPage.Remove);
        }

        /// <summary>
        /// An older installer found this, newer, one and passed on what the user had chosen in it:
        /// take those choices and go straight to installing.
        /// </summary>
        public void ContinueInstall(string[] args)
        {
            string dir = Installer.ArgAfter(args, "--dir");
            if (!string.IsNullOrWhiteSpace(dir)) SetupPath.Text = dir;
            SetupDesktop.IsChecked = Installer.ArgAfter(args, "--desktop") != "0";
            SetupAutostart.IsChecked = Installer.ArgAfter(args, "--autostart") == "1";
            _handedOver = true;
            GoTo(SetupPage.Options);   // where a path that turns out to be wrong can be corrected
            RunInstall();
        }

        void GoTo(SetupPage page)
        {
            _setupPage = page;
            PageWelcome.Visibility = page == SetupPage.Welcome ? Visibility.Visible : Visibility.Collapsed;
            PageOptions.Visibility = page == SetupPage.Options ? Visibility.Visible : Visibility.Collapsed;
            PageWorking.Visibility = page == SetupPage.Working ? Visibility.Visible : Visibility.Collapsed;
            PageDone.Visibility = page == SetupPage.Done ? Visibility.Visible : Visibility.Collapsed;
            PageRemove.Visibility = page == SetupPage.Remove ? Visibility.Visible : Visibility.Collapsed;

            string back = null, next = null, step = "";
            bool install = _setup == SetupMode.Install;
            switch (page)
            {
                case SetupPage.Welcome: next = "Далее"; step = "Шаг 1 из 4"; break;
                case SetupPage.Options: back = "Назад"; next = "Установить"; step = "Шаг 2 из 4"; break;
                case SetupPage.Working: step = install ? "Шаг 3 из 4" : ""; break;
                case SetupPage.Done: next = install && !_setupFailed ? "Готово" : "Закрыть"; step = install ? "Шаг 4 из 4" : ""; break;
                case SetupPage.Remove: back = "Отмена"; next = "Удалить"; break;
            }
            SetupBack.Content = back;
            SetupBack.Visibility = back == null ? Visibility.Collapsed : Visibility.Visible;
            SetupNext.Content = next;
            SetupNext.Visibility = next == null ? Visibility.Collapsed : Visibility.Visible;
            SetupStep.Text = step;
        }

        void SetupBack_Click(object sender, RoutedEventArgs e)
        {
            if (_setupPage == SetupPage.Options) GoTo(SetupPage.Welcome);
            else if (_setupPage == SetupPage.Remove) Close();
        }

        void SetupNext_Click(object sender, RoutedEventArgs e)
        {
            switch (_setupPage)
            {
                case SetupPage.Welcome: GoTo(SetupPage.Options); break;
                case SetupPage.Options: RunInstall(); break;
                case SetupPage.Remove: RunUninstall(); break;
                case SetupPage.Done:
                    if (_setup == SetupMode.Install && !_setupFailed && SetupLaunch.IsChecked == true)
                    {
                        try { Process.Start(_installedExe); } catch { }
                    }
                    Close();
                    break;
            }
        }

        void SetupBrowse_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Папка, в которую установить " + Installer.AppName;
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

                string dir = dialog.SelectedPath;
                if (!string.Equals(Path.GetFileName(dir), Installer.AppName, StringComparison.OrdinalIgnoreCase))
                    dir = Path.Combine(dir, Installer.AppName);
                SetupPath.Text = dir;
                SetupPathError.Text = "";
            }
        }

        async void RunInstall()
        {
            string dir = SetupPath.Text.Trim().Trim('"');
            if (dir.Length == 0 || !Path.IsPathRooted(dir) || dir.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                SetupPathError.Text = "Укажите полный путь к папке, например " + Installer.DefaultDir;
                return;
            }
            SetupPathError.Text = "";
            bool desktop = SetupDesktop.IsChecked == true, autostart = SetupAutostart.IsChecked == true;

            _setupBusy = true;
            SetupDoing.Text = "Установка…";
            SetupBar.IsIndeterminate = true;
            GoTo(SetupPage.Working);

            string note = "";

            // Installers get passed around for years; what they install should not be that old.
            // If a newer version is out, its installer is fetched and carries on from this very point.
            if (!_handedOver)
            {
                ReleaseInfo newer = null;
                bool handed = false;
                try
                {
                    SetupStatus.Text = "Проверяю, нет ли версии новее";
                    newer = await Installer.FindNewerAsync();
                    if (newer != null)
                    {
                        SetupDoing.Text = "Обновление установщика…";
                        SetupStatus.Text = "Вышла версия " + newer.Tag.TrimStart('v') + " — скачиваю её установщик";
                        SetupBar.IsIndeterminate = false;
                        string setup = await Installer.DownloadSetupAsync(newer, new Progress<double>(p => SetupBar.Value = p * 100));
                        Installer.HandOver(setup, dir, desktop, autostart);
                        handed = true;
                    }
                }
                catch (Exception) { }   // GitHub out of reach, a download cut short: this version will have to do

                if (handed)
                {
                    _setupBusy = false;
                    Close();
                    return;
                }
                if (newer != null)
                    note += "\n\nВышла версия " + newer.Tag.TrimStart('v') + ", но скачать её установщик не удалось — установлена версия " + Installer.Version + ".";
                SetupDoing.Text = "Установка…";
                SetupBar.Value = 0;
                SetupBar.IsIndeterminate = true;
            }

            try
            {
                SetupStatus.Text = "Копирую файлы и создаю ярлыки";
                _installedExe = await Task.Run(() =>
                {
                    Installer.StopRunning();
                    return Installer.Install(dir, desktop);
                });

                var settings = Settings.Load();
                try
                {
                    await Task.Run(() => Autostart.Set(autostart, _installedExe));
                    settings.Autostart = autostart;
                }
                catch (Exception ex)
                {
                    settings.Autostart = false;
                    note += "\n\nАвтозапуск включить не удалось: " + ex.Message + ". Его можно включить в самом лаунчере.";
                }
                settings.Save();

                if (!Engine.IsInstalled)
                {
                    try
                    {
                        SetupStatus.Text = "Проверяю последнюю версию zapret на GitHub";
                        var release = await Updater.GetLatestAsync();
                        SetupStatus.Text = "Скачиваю компоненты zapret " + release.Tag;
                        SetupBar.IsIndeterminate = false;
                        await Updater.InstallAsync(release, new Progress<double>(p => SetupBar.Value = p * 100));
                    }
                    catch (Exception ex)
                    {
                        note += "\n\nКомпоненты zapret скачать не удалось: " + ex.Message + ". Лаунчер скачает их при первом запуске.";
                    }
                }

                if (!TgProxy.IsInstalled)
                {
                    try
                    {
                        SetupBar.IsIndeterminate = true;
                        SetupStatus.Text = "Проверяю последнюю версию TG WS Proxy на GitHub";
                        var proxy = await TgProxy.GetLatestAsync();
                        SetupStatus.Text = "Скачиваю прокси для Telegram (TG WS Proxy " + proxy.Tag + ")";
                        SetupBar.Value = 0;
                        SetupBar.IsIndeterminate = false;
                        await TgProxy.InstallAsync(proxy, new Progress<double>(p => SetupBar.Value = p * 100));
                    }
                    catch (Exception ex)
                    {
                        note += "\n\nПрокси для Telegram скачать не удалось: " + ex.Message + ". Его можно скачать на вкладке TG WS Proxy.";
                    }
                }

                SetupDoneTitle.Text = "Установлено";
                SetupDoneText.Text = Installer.AppName + " установлен в папку " + dir + "." + note;
                SetupLaunch.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                _setupFailed = true;
                SetupDoneTitle.Text = "Не удалось установить";
                SetupDoneText.Text = ex.Message;
                SetupLaunch.Visibility = Visibility.Collapsed;
            }

            _setupBusy = false;
            GoTo(SetupPage.Done);
        }

        async void RunUninstall()
        {
            bool keep = SetupKeep.IsChecked == true;

            _setupBusy = true;
            SetupDoing.Text = "Удаление…";
            SetupStatus.Text = "Останавливаю лаунчер и удаляю файлы";
            SetupBar.IsIndeterminate = true;
            GoTo(SetupPage.Working);

            try
            {
                await Task.Run(() => Installer.Uninstall(_setupDir, keep));
                SetupDoneTitle.Text = "Удалено";
                SetupDoneText.Text = Installer.AppName + " удалён с компьютера." +
                    (keep ? "\n\nНастройки и ваши списки сохранены — при повторной установке они подхватятся сами." : "");
            }
            catch (Exception ex)
            {
                _setupFailed = true;
                SetupDoneTitle.Text = "Не удалось удалить";
                SetupDoneText.Text = ex.Message;
            }
            SetupLaunch.Visibility = Visibility.Collapsed;
            _selfDelete = true;

            _setupBusy = false;
            GoTo(SetupPage.Done);
        }

        // The uninstaller runs from a copy in %TEMP%; once it exits, that copy removes itself.
        void SetupCleanup()
        {
            if (!_selfDelete) return;
            string self = Installer.SelfPath;
            if (!self.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & del /q \"" + self + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch { }
        }
    }
}
