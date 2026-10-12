using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace ZapretLauncher
{
    public partial class App : Application
    {
        static Mutex _mutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            if (RunSetup(e.Args)) return;

            bool created;
            _mutex = new Mutex(true, @"Local\ZapretLauncher.Instance", out created);
            var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ZapretLauncher.Show");

            // Second launch: just bring the first window to the front and exit.
            if (!created)
            {
                showEvent.Set();
                Shutdown();
                return;
            }

            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            bool autostart = e.Args.Contains("--autostart");
            var window = new MainWindow(autostart);
            MainWindow = window;
            if (!autostart) window.Show();   // autostart: tray only, no window
            window.Initialize();

            var listener = new Thread(() =>
            {
                while (showEvent.WaitOne())
                    window.Dispatcher.BeginInvoke(new Action(window.ShowFromTray));
            });
            listener.IsBackground = true;
            listener.Start();
        }

        /// <summary>
        /// The same exe is the installer (named *Setup.exe, or started with --setup) and the
        /// uninstaller (--uninstall). Returns true if one of those took over the start-up.
        /// </summary>
        bool RunSetup(string[] args)
        {
            string exe = Installer.SelfPath;
            SetupMode mode;
            string dir = null;

            int un = Array.IndexOf(args, "--uninstall");
            if (un >= 0)
            {
                if (un + 1 >= args.Length)
                {
                    // Started from the install folder, which this very file keeps locked:
                    // hand over to a copy in %TEMP% that is free to remove it.
                    string copy = Path.Combine(Path.GetTempPath(), "ZapretLauncherUninstall.exe");
                    File.Copy(exe, copy, true);
                    Process.Start(copy, "--uninstall \"" + Path.GetDirectoryName(exe).TrimEnd('\\') + "\"");
                    Shutdown();
                    return true;
                }
                mode = SetupMode.Uninstall;
                dir = args[un + 1];
            }
            else if (args.Contains("--setup") ||
                     Path.GetFileNameWithoutExtension(exe).EndsWith("Setup", StringComparison.OrdinalIgnoreCase))
            {
                mode = SetupMode.Install;
            }
            else return false;

            var window = new MainWindow(false, mode, dir);
            MainWindow = window;
            window.Show();
            return true;
        }
    }
}
