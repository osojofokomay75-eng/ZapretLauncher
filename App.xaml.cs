using System;
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
    }
}
