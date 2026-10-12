using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace ZapretLauncher
{
    public partial class MainWindow : Window
    {
        enum UiState { Missing, Installing, Idle, Starting, Testing, Running }

        readonly Settings _s = Settings.Load();
        readonly Runner _runner = new Runner();
        readonly bool _autostartLaunch;

        System.Windows.Forms.NotifyIcon _tray;
        System.Windows.Forms.ToolStripItem _trayToggle;
        List<Strategy> _strategies = new List<Strategy>();
        CancellationTokenSource _cts;
        UiState _state = UiState.Idle;
        bool _suppress, _exiting, _balloonShown, _cleaned;
        string _runningName = "";

        public MainWindow(bool autostartLaunch)
        {
            _autostartLaunch = autostartLaunch;
            InitializeComponent();

            var work = SystemParameters.WorkArea;
            if (Height > work.Height) Height = work.Height - 20;

            SetupTray();
        }

        // ---------------------------------------------------------- startup
        public async void Initialize()
        {
            _runner.Crashed += () => Dispatcher.BeginInvoke(new Action(OnCrashed));

            GameToggle.IsChecked = _s.GameFilter;
            AutostartToggle.IsChecked = _s.Autostart;
            RefreshVersion();

            try
            {
                if (Engine.IsInstalled)
                {
                    ReloadStrategies();
                    SetState(UiState.Idle);
                    if (_autostartLaunch) await StartAsync();
                }
                else
                {
                    SetState(UiState.Missing);
                    if (!_autostartLaunch) await InstallAsync(false);
                }
            }
            catch (Exception ex) { Log("Ошибка: " + ex.Message); }
        }

        void SetupTray()
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Открыть", null, (s, e) => ShowFromTray());
            _trayToggle = menu.Items.Add("Включить", null,
                (s, e) => Dispatcher.BeginInvoke(new Action(() => Power_Click(null, null))));
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Выход", null, (s, e) => ExitApp());

            _tray = new System.Windows.Forms.NotifyIcon
            {
                ContextMenuStrip = menu,
                Text = "Zapret Launcher",
                Visible = true
            };
            _tray.MouseClick += (s, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowFromTray();
            };
        }

        public void ShowFromTray()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
        }

        // ------------------------------------------------------------ state
        static Color Hex(string s) { return (Color)ColorConverter.ConvertFromString(s); }

        void SetState(UiState st, string sub = null)
        {
            _state = st;

            string title, subtitle;
            Color color, disc;
            double glow = 0;
            bool pulse = false;

            switch (st)
            {
                case UiState.Missing:
                    title = "Нужна установка"; subtitle = "Нажмите, чтобы скачать компоненты zapret";
                    color = Hex("#BDBDBD"); disc = Hex("#161616"); glow = 0.3; break;
                case UiState.Installing:
                    title = "Загрузка…"; subtitle = "Скачиваю и распаковываю zapret";
                    color = Hex("#BDBDBD"); disc = Hex("#161616"); pulse = true; break;
                case UiState.Starting:
                    title = "Запуск…"; subtitle = "Подождите секунду";
                    color = Hex("#D9D9D9"); disc = Hex("#1C1C1C"); pulse = true; break;
                case UiState.Testing:
                    title = "Подбор стратегии"; subtitle = "Нажмите, чтобы отменить";
                    color = Hex("#D9D9D9"); disc = Hex("#1C1C1C"); pulse = true; break;
                case UiState.Running:
                    title = "Работает"; subtitle = "Обход блокировок включён";
                    color = Hex("#FFFFFF"); disc = Hex("#222222"); glow = 0.55; break;
                default:
                    title = "Отключено"; subtitle = "Нажмите, чтобы включить";
                    color = Hex("#6B6B6B"); disc = Hex("#161616"); break;
            }
            if (sub != null) subtitle = sub;

            StatusText.Text = title;
            SubText.Text = subtitle;
            StatusText.Foreground = new SolidColorBrush(st == UiState.Running ? color : Hex("#F2F2F2"));
            Tint(color, disc, glow, pulse);

            bool locked = st == UiState.Installing || st == UiState.Starting || st == UiState.Testing;
            StrategyList.IsEnabled = !locked;
            GameToggle.IsEnabled = !locked;
            AutoButton.IsEnabled = !locked && _strategies.Count > 0;
            UpdateButton.IsEnabled = !locked;
            PowerButton.IsEnabled = st != UiState.Installing && st != UiState.Starting;

            // tray + window icon follow the state
            var icon = IconFactory.Make(System.Drawing.Color.FromArgb(color.R, color.G, color.B));
            var old = _tray.Icon;
            _tray.Icon = icon;
            if (old != null) old.Dispose();
            _tray.Text = ("Zapret Launcher — " + title).Substring(0, Math.Min(63, ("Zapret Launcher — " + title).Length));
            Icon = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            _trayToggle.Text = st == UiState.Running ? "Выключить" : "Включить";
            _trayToggle.Enabled = st == UiState.Idle || st == UiState.Running || st == UiState.Missing;
        }

        void Tint(Color color, Color disc, double glow, bool pulse)
        {
            var d = TimeSpan.FromMilliseconds(300);
            ((SolidColorBrush)Ring.Stroke).BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, d));
            ((SolidColorBrush)PowerIcon.Stroke).BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, d));
            ((SolidColorBrush)Disc.Fill).BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(disc, d));
            GlowFx.BeginAnimation(DropShadowEffect.ColorProperty, new ColorAnimation(color, d));

            if (pulse)
            {
                GlowFx.BeginAnimation(DropShadowEffect.OpacityProperty,
                    new DoubleAnimation(0.15, 0.8, TimeSpan.FromMilliseconds(750))
                    {
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever
                    });
            }
            else
            {
                GlowFx.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(glow, d));
            }
        }

        void Log(string message) { LogText.Text = message; }

        void RefreshVersion()
        {
            VersionText.Text = "Компоненты zapret: " + (Engine.InstalledVersion ?? "не установлены");
        }

        void ReloadStrategies()
        {
            _strategies = Engine.ListStrategies();
            _suppress = true;
            StrategyList.ItemsSource = _strategies;
            var sel = _strategies.FirstOrDefault(x => x.Name == _s.Strategy) ?? _strategies.FirstOrDefault();
            StrategyList.SelectedItem = sel;
            _suppress = false;
            if (sel != null) StrategyList.ScrollIntoView(sel);
            AutoButton.IsEnabled = _strategies.Count > 0;
        }

        static string FirstLine(string text, string fallback)
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            return text.Split('\n')[0].Trim();
        }

        // ---------------------------------------------------------- actions
        async void Power_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                switch (_state)
                {
                    case UiState.Missing: await InstallAsync(false); break;
                    case UiState.Idle: await StartAsync(); break;
                    case UiState.Running: await StopAsync(); break;
                    case UiState.Testing: if (_cts != null) _cts.Cancel(); break;
                }
            }
            catch (Exception ex) { Log("Ошибка: " + ex.Message); }
        }

        async Task StartAsync()
        {
            var st = StrategyList.SelectedItem as Strategy;
            if (st == null)
            {
                Log("Стратегии не найдены — нажмите «Обновить»");
                return;
            }

            SetState(UiState.Starting, "Стратегия: " + st.Name);
            try
            {
                await Task.Run(() => _runner.Start(st, _s.GameFilter));
                await Task.Delay(1500);
                if (!_runner.IsRunning)
                    throw new Exception(FirstLine(_runner.LastOutput, "winws.exe сразу завершился"));

                _runningName = st.Name;
                SetState(UiState.Running, "Стратегия: " + st.Name);
                Log("Обход включён");
            }
            catch (Exception ex)
            {
                await Task.Run(() => _runner.Stop());
                SetState(UiState.Idle);
                Log("Не удалось запустить: " + ex.Message);
            }
        }

        async Task StopAsync()
        {
            await Task.Run(() => _runner.Stop());
            SetState(UiState.Idle);
            Log("Обход выключен");
        }

        void OnCrashed()
        {
            if (_state != UiState.Running) return;
            SetState(UiState.Idle);
            Log("winws.exe завершился: " + FirstLine(_runner.LastOutput, "неизвестная причина"));
        }

        async void Strategy_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suppress) return;
            var st = StrategyList.SelectedItem as Strategy;
            if (st == null) return;

            _s.Strategy = st.Name;
            _s.Save();
            if (_state == UiState.Running)
            {
                await StopAsync();
                await StartAsync();
            }
        }

        async void Game_Click(object sender, RoutedEventArgs e)
        {
            _s.GameFilter = GameToggle.IsChecked == true;
            _s.Save();
            if (_state == UiState.Running)
            {
                await StopAsync();
                await StartAsync();
            }
        }

        void Autostart_Click(object sender, RoutedEventArgs e)
        {
            bool on = AutostartToggle.IsChecked == true;
            try
            {
                Autostart.Set(on);
                _s.Autostart = on;
                _s.Save();
                Log(on ? "Автозапуск включён" : "Автозапуск выключен");
            }
            catch (Exception ex)
            {
                AutostartToggle.IsChecked = !on;
                Log("Автозапуск: " + ex.Message);
            }
        }

        async void Auto_Click(object sender, RoutedEventArgs e)
        {
            if (_strategies.Count == 0) return;

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            Strategy winner = null;

            SetState(UiState.Testing);
            try
            {
                if (_runner.IsRunning) await Task.Run(() => _runner.Stop());

                for (int i = 0; i < _strategies.Count && winner == null; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var st = _strategies[i];
                    Log("Проверка " + (i + 1) + " из " + _strategies.Count + ": " + st.Name);
                    SubText.Text = st.Name;

                    try { await Task.Run(() => _runner.Start(st, _s.GameFilter), ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { continue; }

                    await Task.Delay(1800, ct);
                    if (!_runner.IsRunning) continue;

                    if (await Probe.OkAsync(ct)) winner = st;
                    else await Task.Run(() => _runner.Stop());
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log("Ошибка подбора: " + ex.Message); }

            if (winner != null)
            {
                _suppress = true;
                StrategyList.SelectedItem = winner;
                _suppress = false;
                StrategyList.ScrollIntoView(winner);
                _s.Strategy = winner.Name;
                _s.Save();
                _runningName = winner.Name;
                SetState(UiState.Running, "Стратегия: " + winner.Name);
                Log("Найдена рабочая стратегия: " + winner.Name);
            }
            else
            {
                await Task.Run(() => _runner.Stop());
                SetState(UiState.Idle);
                Log(ct.IsCancellationRequested
                    ? "Подбор отменён"
                    : "Рабочая стратегия не найдена — попробуйте вручную или обновите компоненты");
            }
        }

        async void Update_Click(object sender, RoutedEventArgs e)
        {
            try { await InstallAsync(true); }
            catch (Exception ex) { Log("Ошибка: " + ex.Message); }
        }

        async Task InstallAsync(bool update)
        {
            bool wasRunning = _state == UiState.Running;
            bool skipped = false;

            SetState(UiState.Installing);
            DownloadBar.Visibility = Visibility.Visible;
            DownloadBar.IsIndeterminate = true;

            try
            {
                Log("Проверяю последнюю версию на GitHub…");
                var rel = await Updater.GetLatestAsync();

                if (update && Engine.IsInstalled && rel.Tag == Engine.InstalledVersion)
                {
                    skipped = true;
                    Log("Уже установлена последняя версия (" + rel.Tag + ")");
                }
                else
                {
                    await Task.Run(() => _runner.Stop());
                    Log("Скачиваю версию " + rel.Tag + "…");
                    DownloadBar.IsIndeterminate = false;
                    var progress = new Progress<double>(p => DownloadBar.Value = p * 100);
                    await Updater.InstallAsync(rel, progress);
                    Log("Установлено: " + rel.Tag);
                }
            }
            catch (Exception ex)
            {
                Log("Не удалось скачать: " + ex.Message);
            }

            DownloadBar.Visibility = Visibility.Collapsed;
            RefreshVersion();

            if (skipped && wasRunning && _runner.IsRunning)
            {
                SetState(UiState.Running, "Стратегия: " + _runningName);
                return;
            }

            if (Engine.IsInstalled)
            {
                ReloadStrategies();
                SetState(UiState.Idle);
                if (wasRunning) await StartAsync();
            }
            else
            {
                SetState(UiState.Missing);
            }
        }

        // -------------------------------------------------------- chrome
        void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        void Min_Click(object sender, RoutedEventArgs e) { WindowState = WindowState.Minimized; }

        void Close_Click(object sender, RoutedEventArgs e) { Close(); }

        void PowerButton_Enter(object sender, MouseEventArgs e) { ScaleHero(1.05); }

        void PowerButton_Leave(object sender, MouseEventArgs e) { ScaleHero(1.0); }

        void ScaleHero(double to)
        {
            var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(160)) { EasingFunction = new QuadraticEase() };
            HeroScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            HeroScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        // The list scrolls by pixels; the wheel eases towards a target offset instead of jumping.
        static readonly DependencyProperty ListOffsetProperty = DependencyProperty.Register(
            "ListOffset", typeof(double), typeof(MainWindow), new PropertyMetadata(0.0, (d, e) =>
            {
                var w = (MainWindow)d;
                if (w._listScroll != null) w._listScroll.ScrollToVerticalOffset((double)e.NewValue);
            }));

        System.Windows.Controls.ScrollViewer _listScroll;
        double _scrollTarget;
        bool _scrolling;

        static T FindChild<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var hit = child as T ?? FindChild<T>(child);
                if (hit != null) return hit;
            }
            return null;
        }

        void StrategyList_Wheel(object sender, MouseWheelEventArgs e)
        {
            if (_listScroll == null) _listScroll = FindChild<System.Windows.Controls.ScrollViewer>(StrategyList);
            if (_listScroll == null) return;
            e.Handled = true;

            // Start from where the list really is unless a wheel animation is still in flight.
            if (!_scrolling) _scrollTarget = _listScroll.VerticalOffset;
            _scrollTarget = Math.Max(0, Math.Min(_listScroll.ScrollableHeight, _scrollTarget - e.Delta * 0.8));

            var a = new DoubleAnimation(_listScroll.VerticalOffset, _scrollTarget, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            double target = _scrollTarget;
            a.Completed += (s, ev) => { if (target == _scrollTarget) _scrolling = false; };
            _scrolling = true;
            BeginAnimation(ListOffsetProperty, a);
        }

        void Folder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = Engine.FindRoot() ?? Paths.Root;
                System.IO.Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex) { Log(ex.Message); }
        }

        // -------------------------------------------------------- closing
        protected override void OnClosing(CancelEventArgs e)
        {
            // While the bypass is running, the close button just hides the window to the tray.
            if (!_exiting && _state == UiState.Running)
            {
                e.Cancel = true;
                Hide();
                if (!_balloonShown)
                {
                    _balloonShown = true;
                    _tray.ShowBalloonTip(2500, "Zapret Launcher",
                        "Работает в фоне. Выйти можно через значок в трее.",
                        System.Windows.Forms.ToolTipIcon.Info);
                }
                return;
            }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            Cleanup();
            Application.Current.Shutdown();
        }

        void Cleanup()
        {
            if (_cleaned) return;
            _cleaned = true;
            try { _tray.Visible = false; _tray.Dispose(); } catch { }
            _runner.Stop();
        }

        void ExitApp()
        {
            _exiting = true;
            Close();
        }
    }
}
