using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace ZapretLauncher
{
    /// <summary>
    /// The installer is the launcher itself under another name: started as *Setup.exe (or with --setup)
    /// it shows the setup wizard, and with --uninstall the removal wizard, both inside the main window.
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
        /// Removes everything Install created. With keepSettings the choices the user made
        /// (settings.ini and their own *user*.txt lists) stay for a later re-install.
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
                if (!settings && !userList) TryDelete(file);
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
