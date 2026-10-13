using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ZapretLauncher
{
    /// <summary>
    /// The second tab: TG WS Proxy, a local proxy for Telegram. It mirrors the zapret tab - a large power
    /// button in the middle - and is meant to need one press: the button starts the proxy and hands it to
    /// Telegram. How to connect is told in a window opened by "Information"; the proxy's switches live on
    /// the Settings tab. Switching between the tabs is here as well.
    /// </summary>
    public partial class MainWindow
    {
        enum ProxyState { Missing, Installing, Idle, Starting, Running }
        enum Tab { Zapret, Proxy, Settings }

        // "photos and video do not load" is cured by a different DC -> IP table; these are the ones worth trying
        static readonly string[][] DcTables =
        {
            new[] { "2:149.154.167.220", "4:149.154.167.220" },
            new[] { "4:149.154.167.220" },
            new string[0]
        };
        static readonly string[] DcTableNames = { "обычный", "только DC 4", "без привязки адресов" };

        readonly ProxyRunner _proxy = new ProxyRunner();
        ProxyConfig _px;
        ProxyState _pxState = ProxyState.Idle;
        Tab _tab = Tab.Zapret;      // the tab that is chosen
        Tab _shown = Tab.Zapret;    // the tab whose view is on screen; behind _tab while one fades into the other
        System.Windows.Threading.DispatcherTimer _tabTimer;
        bool _pxOwnTray;    // out of sight the proxy would not start this time, so it runs with its own tray icon
        Action<string> _openLink = link => Process.Start(link);

        void InitProxy()
        {
            _px = ProxyConfig.Load();
            _proxy.Crashed += () => Dispatcher.BeginInvoke(new Action(OnProxyStopped));
            PxCf.IsChecked = _px.CfProxy;
            PxH2.IsChecked = _px.H2;
            PxAuto.IsChecked = _s.ProxyAuto;
            SetProxyState(TgProxy.IsInstalled ? ProxyState.Idle : ProxyState.Missing);

            if (_s.Tab == "proxy") TabProxy.IsChecked = true;   // Tab_Checked opens it
        }

        // ------------------------------------------------------------- tabs
        // Checked rather than Click, so that the tab also follows a choice made from the keyboard
        void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (SettingsView == null) return;   // raised while the window is still being put together
            ShowTab(ReferenceEquals(sender, TabProxy) ? Tab.Proxy : ReferenceEquals(sender, TabSettings) ? Tab.Settings : Tab.Zapret);
        }

        // The underline leaves at once; the views take their time: the old one fades out and, a third of a
        // second after the click, the new one fades in.
        void ShowTab(Tab tab)
        {
            if (tab == _tab) return;
            _tab = tab;
            HideHint();
            CloseModal();
            MoveTabBar(true);
            RefreshVersion();

            // the launcher opens on the service last looked at; Settings is a place to visit, not to start from
            string name = tab == Tab.Proxy ? "proxy" : "zapret";
            if (tab != Tab.Settings && _s.Tab != name)
            {
                _s.Tab = name;
                _s.Save();
            }

            if (!IsLoaded)
            {
                SwapViews(false);   // nothing is on screen yet to fade
                return;
            }

            var old = TabViews()[(int)_shown];
            old.IsHitTestVisible = false;   // a view on its way out takes no more clicks
            var leave = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
            old.BeginAnimation(OpacityProperty, leave);
            Frost.BeginAnimation(OpacityProperty, leave);   // the panels' glass is drawn apart from them, so it fades apart

            if (_tabTimer == null)
            {
                _tabTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(340) };
                _tabTimer.Tick += (s, e) =>
                {
                    _tabTimer.Stop();
                    SwapViews(true);
                };
            }
            // another tab chosen in the meantime simply takes this one's place
            _tabTimer.Stop();
            _tabTimer.Start();
        }

        FrameworkElement[] TabViews() { return new FrameworkElement[] { ZapretView, ProxyView, SettingsView }; }

        // Puts the chosen tab's view on screen in place of the one that was there.
        void SwapViews(bool fade)
        {
            var views = TabViews();
            for (int i = 0; i < views.Length; i++)
            {
                views[i].Visibility = i == (int)_tab ? Visibility.Visible : Visibility.Collapsed;
                views[i].IsHitTestVisible = true;
            }
            _shown = _tab;

            // the glass has to be cut out again for the panels of the tab now on screen
            if (IsLoaded)
            {
                UpdateLayout();
                _glass.Clear();
                CollectGlass(Shell);
                _frostKey = null;
                UpdateFrost();
            }

            var arrive = fade ? new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) : null;
            foreach (var view in views) view.BeginAnimation(OpacityProperty, ReferenceEquals(view, views[(int)_tab]) ? arrive : null);
            Frost.BeginAnimation(OpacityProperty, arrive);
        }

        // Slides the underline under the chosen tab.
        void MoveTabBar(bool animate)
        {
            var tab = _tab == Tab.Proxy ? TabProxy : _tab == Tab.Settings ? TabSettings : TabZapret;
            if (tab.ActualWidth <= 0) return;   // not laid out yet; the window calls again once it is
            double x = tab.TranslatePoint(new Point(tab.ActualWidth / 2, 0), TabStrip).X - TabBar.Width / 2;

            if (!animate || TabBar.Opacity == 0)
            {
                TabBarAt.BeginAnimation(TranslateTransform.XProperty, null);
                TabBarAt.X = x;
                TabBar.Opacity = 1;
                return;
            }

            TabBarAt.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(x, TimeSpan.FromMilliseconds(360)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
        }

        // The white dot beside a tab's name: there while that tab's service is running, gone when it is not.
        void SetDot(System.Windows.Controls.RadioButton tab, bool on)
        {
            tab.ApplyTemplate();
            var dot = tab.Template.FindName("Dot", tab) as UIElement;
            if (dot == null) return;
            dot.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(on ? 350 : 250)));
        }

        // ------------------------------------------------------------ state
        void SetProxyState(ProxyState st)
        {
            _pxState = st;

            string title, subtitle;
            Color color;
            double glow = 0;
            bool pulse = false;

            switch (st)
            {
                case ProxyState.Missing:
                    title = "Нужна загрузка"; subtitle = "Нажмите, чтобы скачать TG WS Proxy и включить его";
                    color = Hex("#BDBDBD"); glow = 0.3; break;
                case ProxyState.Installing:
                    title = "Загрузка…"; subtitle = "Скачиваю TG WS Proxy с GitHub";
                    color = Hex("#BDBDBD"); pulse = true; break;
                case ProxyState.Starting:
                    title = "Запуск…"; subtitle = "Прокси поднимается, это несколько секунд";
                    color = Hex("#D9D9D9"); pulse = true; break;
                case ProxyState.Running:
                    title = "Работает"; subtitle = "Прокси для Telegram включён";
                    color = _accent; glow = 0.7; break;
                default:
                    title = "Отключено"; subtitle = "Нажмите, чтобы включить прокси для Telegram";
                    color = Hex("#8A8A8A"); break;
            }

            PxStatus.Text = title;
            PxSub.Text = subtitle;
            PxStatus.Foreground = new SolidColorBrush(st == ProxyState.Running ? color : Hex("#F2F2F2"));
            Tint(PxRing, PxIcon, PxHaloIn, PxHaloOut, color, glow, pulse, st == ProxyState.Running);

            bool locked = st == ProxyState.Installing || st == ProxyState.Starting;
            PxPower.IsEnabled = !locked;
            // only dimmed while the proxy is off: a disabled button would not show its hint
            PxConnect.Opacity = st == ProxyState.Running ? 1 : 0.45;
            PxCf.IsEnabled = PxH2.IsEnabled = PxMedia.IsEnabled = !locked;

            SetDot(TabProxy, st == ProxyState.Running);
            UpdateTray();
        }

        async void PxPower_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                switch (_pxState)
                {
                    case ProxyState.Missing:
                        await ProxyInstallAsync(false);
                        if (_pxState == ProxyState.Idle) await ProxyStartAsync(true);
                        break;
                    case ProxyState.Idle: await ProxyStartAsync(true); break;
                    case ProxyState.Running: await ProxyStopAsync(); break;
                }
            }
            catch (Exception ex) { Log("Ошибка: " + ex.Message); }
        }

        // what Telegram has to know to use the proxy; when it changes, Telegram has to be told again
        string LinkId() { return _px.LinkHost + ":" + _px.Port + ":" + _px.Secret.Substring(0, 8); }

        /// <summary>
        /// byUser: the user pressed the button themselves, so the first time round Telegram may be brought up
        /// to accept the proxy. A start along with the launcher never opens anything.
        /// </summary>
        async Task ProxyStartAsync(bool byUser)
        {
            if (!TgProxy.IsInstalled)
            {
                SetProxyState(ProxyState.Missing);
                return;
            }

            SetProxyState(ProxyState.Starting);
            try
            {
                bool foreign = await Task.Run(() =>
                {
                    _proxy.Stop();
                    return ProxyRunner.ForeignRunning;
                });
                if (foreign) throw new Exception("TG WS Proxy уже запущен отдельно — закройте его через его значок в трее");

                // something else may already sit on the port: move to a free one rather than fail
                if (!ProxyRunner.PortFree(_px.LinkHost, _px.Port)) _px.Port = ProxyRunner.FreePort();
                _px.Save();

                bool open = await ProxyLaunchAsync(!_pxOwnTray);
                if (!_proxy.IsRunning && !_pxOwnTray)
                {
                    // out of sight it did not survive; in the open, with its own tray icon, it might
                    _pxOwnTray = true;
                    open = await ProxyLaunchAsync(false);
                }
                if (!_proxy.IsRunning)
                    throw new Exception(TgProxy.LastLogLine() ?? "процесс сразу завершился — возможно, его заблокировал антивирус");

                SetProxyState(ProxyState.Running);
                if (!open)
                    Log("Прокси запущен, но порт " + _px.Port + " пока не отвечает — загляните в логи");
                else if (_s.ProxyLinked == LinkId())
                    Log("Прокси включён");
                else if (byUser)
                    Log(OpenInTelegram()
                        ? "Прокси включён. В Telegram откроется окно — подтвердите в нём подключение"
                        : "Прокси включён. Telegram не открылся — скопируйте ссылку и откройте её в нём");
                else
                    Log("Прокси включён. Нажмите «Подключить», чтобы добавить его в Telegram");
            }
            catch (Exception ex)
            {
                await Task.Run(() => _proxy.Stop());
                SetProxyState(ProxyState.Idle);
                Log("Не удалось запустить прокси: " + ex.Message);
            }
        }

        // Starts the process and waits for its port: the one-file build unpacks itself first, which takes seconds.
        async Task<bool> ProxyLaunchAsync(bool hidden)
        {
            await Task.Run(() => _proxy.Start(hidden));
            bool open = false;
            for (int i = 0; i < 60 && !open && _proxy.IsRunning; i++)
            {
                open = await ProxyRunner.PortOpenAsync(_px.LinkHost, _px.Port, 300);
                if (!open) await Task.Delay(250);
            }
            return open;
        }

        async Task ProxyStopAsync()
        {
            await Task.Run(() => _proxy.Stop());
            SetProxyState(ProxyState.Idle);
            Log("Прокси выключен");
        }

        // new settings only take effect in a fresh process
        async Task ProxyRestartIfRunning()
        {
            if (_pxState != ProxyState.Running) return;
            // locks the power button right away: a click landing in the middle of the restart would be undone by it
            SetProxyState(ProxyState.Starting);
            await ProxyStartAsync(false);
        }

        // the proxy went away on its own
        void OnProxyStopped()
        {
            if (_pxState != ProxyState.Running) return;
            SetProxyState(ProxyState.Idle);
            string last = TgProxy.LastLogLine();
            Log(last != null && last.IndexOf("Tray app exited", StringComparison.OrdinalIgnoreCase) >= 0
                ? "TG WS Proxy закрыт из его собственного меню в трее"
                : "TG WS Proxy завершился: " + (last ?? "причина неизвестна"));
        }

        async Task ProxyInstallAsync(bool update)
        {
            bool wasRunning = _pxState == ProxyState.Running;

            SetProxyState(ProxyState.Installing);
            DownloadBar.Visibility = Visibility.Visible;
            DownloadBar.IsIndeterminate = true;

            try
            {
                Log("Проверяю последнюю версию TG WS Proxy на GitHub…");
                var rel = await TgProxy.GetLatestAsync();

                if (update && TgProxy.IsInstalled && rel.Tag == TgProxy.InstalledVersion)
                {
                    Log("Уже установлена последняя версия (" + rel.Tag + ")");
                }
                else
                {
                    await Task.Run(() => _proxy.Stop());
                    Log("Скачиваю TG WS Proxy " + rel.Tag + "…");
                    DownloadBar.IsIndeterminate = false;
                    var progress = new Progress<double>(p => DownloadBar.Value = p * 100);
                    await TgProxy.InstallAsync(rel, progress);
                    Log("Установлено: TG WS Proxy " + rel.Tag);
                }
            }
            catch (Exception ex)
            {
                Log("Не удалось скачать: " + ex.Message);
            }

            DownloadBar.Visibility = Visibility.Collapsed;
            RefreshVersion();

            if (!TgProxy.IsInstalled)
            {
                SetProxyState(ProxyState.Missing);
                return;
            }
            if (wasRunning && _proxy.IsRunning)
            {
                SetProxyState(ProxyState.Running);
                return;
            }
            SetProxyState(ProxyState.Idle);
            if (wasRunning) await ProxyStartAsync(false);
        }

        // --------------------------------------------------------- settings
        async void PxSwitch_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _px.CfProxy = PxCf.IsChecked == true;
                _px.H2 = PxH2.IsChecked == true;
                _px.Save();
                await ProxyRestartIfRunning();
            }
            catch (Exception ex) { Log("Не удалось сохранить настройки: " + ex.Message); }
        }

        void PxAuto_Click(object sender, RoutedEventArgs e)
        {
            _s.ProxyAuto = PxAuto.IsChecked == true;
            _s.Save();
        }

        // Each press moves on to the next DC -> IP table and restarts the proxy with it.
        async void PxMedia_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var now = _px.DcIp;
                int current = Array.FindIndex(DcTables, t => t.SequenceEqual(now));
                int next = (current + 1) % DcTables.Length;   // a table edited by hand counts as "none of these"
                _px.DcIp = DcTables[next].ToList();
                _px.Save();
                Log("Способ соединения " + (next + 1) + " из " + DcTables.Length + " (" + DcTableNames[next] + "). "
                    + (_pxState == ProxyState.Running ? "Прокси перезапущен — проверьте фото и видео в Telegram" : "Включите прокси и проверьте фото и видео"));
                await ProxyRestartIfRunning();
            }
            catch (Exception ex) { Log("Не удалось сохранить настройки: " + ex.Message); }
        }

        // ---------------------------------------------------------- actions
        /// <summary>Opens the proxy's link, which makes Telegram offer to switch the proxy on. False if nothing took it.</summary>
        bool OpenInTelegram()
        {
            try
            {
                _openLink(_px.Link);
                _s.ProxyLinked = LinkId();
                _s.Save();
                return true;
            }
            catch (Exception) { return false; }
        }

        void ProxyInfo_Click(object sender, RoutedEventArgs e) { OpenModal(ProxyInfoPanel); }

        void PxConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_pxState != ProxyState.Running)
            {
                Log("Сначала включите прокси кнопкой питания");
                return;
            }
            Log(OpenInTelegram()
                ? "В Telegram откроется окно — подтвердите в нём подключение прокси"
                : "Не удалось открыть Telegram — скопируйте ссылку и откройте её в нём вручную");
        }

        void PxCopy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _px.Save();   // a link to a secret that was never written down would stop working after a restart
                Clipboard.SetText(_px.Link);
                Log("Ссылка скопирована — отправьте её себе в «Избранное» и нажмите на неё");
            }
            catch (Exception ex) { Log("Не удалось скопировать: " + ex.Message); }
        }

        void PxLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!System.IO.File.Exists(TgProxy.LogFile))
                {
                    Log("Логов пока нет — прокси ещё не запускался");
                    return;
                }
                Process.Start("notepad.exe", "\"" + TgProxy.LogFile + "\"");
            }
            catch (Exception ex) { Log(ex.Message); }
        }

        void ProxyFolder()
        {
            System.IO.Directory.CreateDirectory(TgProxy.Dir);
            Process.Start("explorer.exe", "\"" + TgProxy.Dir + "\"");
        }
    }
}
