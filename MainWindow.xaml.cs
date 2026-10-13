using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ZapretLauncher
{
    /// <summary>Marks an element as frosted glass; the value is its corner radius.</summary>
    public static class Glass
    {
        public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
            "Radius", typeof(double), typeof(Glass), new PropertyMetadata(double.NaN));

        public static double GetRadius(DependencyObject o) { return (double)o.GetValue(RadiusProperty); }
        public static void SetRadius(DependencyObject o, double value) { o.SetValue(RadiusProperty, value); }
    }

    /// <summary>Text for the window's frosted hint, shown while the pointer rests on the element.</summary>
    public static class Hint
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
            "Text", typeof(string), typeof(Hint), new PropertyMetadata(null, (d, e) =>
            {
                var fe = d as FrameworkElement;
                if (fe == null || e.OldValue != null) return;
                fe.MouseEnter += (s, a) => Owner(fe, w => w.QueueHint(fe));
                fe.MouseLeave += (s, a) => Owner(fe, w => w.HideHint());
                fe.PreviewMouseLeftButtonDown += (s, a) => Owner(fe, w => w.HideHint());
            }));

        public static string GetText(DependencyObject o) { return (string)o.GetValue(TextProperty); }
        public static void SetText(DependencyObject o, string value) { o.SetValue(TextProperty, value); }

        static void Owner(FrameworkElement fe, Action<MainWindow> act)
        {
            var w = Window.GetWindow(fe) as MainWindow;
            if (w != null) act(w);
        }
    }

    public partial class MainWindow : Window
    {
        enum UiState { Missing, Installing, Idle, Starting, Testing, Running }

        readonly Settings _s = Settings.Load();
        readonly Runner _runner = new Runner();
        readonly bool _autostartLaunch;

        System.Windows.Forms.NotifyIcon _tray;
        List<Strategy> _strategies = new List<Strategy>();
        CancellationTokenSource _cts;
        UiState _state = UiState.Idle;
        bool _suppress, _exiting, _balloonShown, _cleaned;
        string _runningName = "";

        public MainWindow(bool autostartLaunch, SetupMode setup = SetupMode.None, string setupDir = null)
        {
            _autostartLaunch = autostartLaunch;
            InitializeComponent();
            if (setup != SetupMode.None) StartSetup(setup, setupDir);

            var work = SystemParameters.WorkArea;
            if (Height > work.Height) Height = work.Height - 20;
            if (Width > work.Width) Width = work.Width - 20;

            BuildSky();
            InitLook();
            Loaded += (s, e) =>
            {
                var source = PresentationSource.FromVisual(this);
                if (source != null && source.CompositionTarget != null) _dpiScale = source.CompositionTarget.TransformToDevice.M22;
                CollectGlass(Shell);
                UpdateFrost();
                UpdateDrift();
                SetupCursor();
            };
            LayoutUpdated += (s, e) => UpdateFrost();
            IsVisibleChanged += (s, e) => UpdateDrift();
            StateChanged += (s, e) => UpdateDrift();

            if (_setup == SetupMode.None) SetupTray();   // the wizard has no tray icon
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Windows 11: dark frame, rounded corners, a quiet border. Older systems ignore these.
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int dark = 1, round = 2, border = 0x00303030;
                DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
                DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
                DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));
            }
            catch { }
        }

        // ----------------------------------------------------------- cursor
        // The dot is drawn by the window, not by Windows, so it can trail the pointer on a spring:
        // it eases into place and swings wide on sharp turns. Clicks still land on the real pointer.
        const double CursorSpeed = 34;      // spring frequency, rad/s: higher follows the mouse tighter
        const double CursorDamping = 0.6;   // below 1 the dot overshoots; lower means more drift

        Point _cursorPos, _cursorTarget;
        Vector _cursorVel;
        TimeSpan _cursorTime;
        bool _cursorReady, _cursorShown, _cursorMoving, _cursorFresh, _pressed;
        double _dpiScale = 1;

        void SetupCursor()
        {
            Cursor = Cursors.None;
            ForceCursor = true;
            _cursorReady = true;
        }

        void ShowCursor(bool on)
        {
            if (on == _cursorShown) return;
            _cursorShown = on;
            CursorLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(120)));
        }

        void MoveCursorTo(Point p)
        {
            _cursorTarget = p;
            if (!_cursorShown)
            {
                // entering the window: start on the pointer instead of flying in from the last spot
                _cursorPos = p;
                _cursorVel = new Vector();
                CursorAt.X = p.X;
                CursorAt.Y = p.Y;
                ShowCursor(true);
            }
            if (_cursorMoving) return;
            _cursorMoving = _cursorFresh = true;
            CompositionTarget.Rendering += CursorTick;
        }

        // Damped spring towards the pointer, stepped once per rendered frame; stops itself when settled.
        void CursorTick(object sender, EventArgs e)
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            double dt = _cursorFresh ? 0 : Math.Min((now - _cursorTime).TotalSeconds, 0.05);
            if (!_cursorFresh && dt <= 0) return;
            _cursorFresh = false;
            _cursorTime = now;

            for (; dt > 0; dt -= 0.004)
            {
                double step = Math.Min(dt, 0.004);
                var pull = (_cursorTarget - _cursorPos) * (CursorSpeed * CursorSpeed) - _cursorVel * (2 * CursorDamping * CursorSpeed);
                _cursorVel += pull * step;
                _cursorPos += _cursorVel * step;
            }

            if ((_cursorTarget - _cursorPos).Length < 0.1 && _cursorVel.Length < 3)
            {
                _cursorPos = _cursorTarget;
                _cursorVel = new Vector();
                _cursorMoving = false;
                CompositionTarget.Rendering -= CursorTick;
            }
            CursorAt.X = _cursorPos.X;
            CursorAt.Y = _cursorPos.Y;
        }

        const int PressMs = 300, ReleaseMs = 460;
        DispatcherTimer _releaseTimer;
        int _pressedAt;

        // A press always plays to the end: on a quick click the release waits until the halo
        // has shrunk all the way, instead of cutting the animation short.
        void PressCursor(bool down)
        {
            if (down == _pressed) return;
            _pressed = down;

            if (_releaseTimer != null) _releaseTimer.Stop();
            if (down)
            {
                _pressedAt = Environment.TickCount;
                AnimateCursor(true);
                return;
            }

            int left = PressMs - unchecked(Environment.TickCount - _pressedAt);
            if (left <= 0)
            {
                AnimateCursor(false);
                return;
            }
            if (_releaseTimer == null)
            {
                _releaseTimer = new DispatcherTimer();
                _releaseTimer.Tick += (s, e) =>
                {
                    _releaseTimer.Stop();
                    if (!_pressed) AnimateCursor(false);
                };
            }
            _releaseTimer.Interval = TimeSpan.FromMilliseconds(left);
            _releaseTimer.Start();
        }

        void AnimateCursor(bool down)
        {
            var time = TimeSpan.FromMilliseconds(down ? PressMs : ReleaseMs);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            var glow = new DoubleAnimation(down ? 0.55 : 1, time) { EasingFunction = ease };
            CursorGlowScale.BeginAnimation(ScaleTransform.ScaleXProperty, glow);
            CursorGlowScale.BeginAnimation(ScaleTransform.ScaleYProperty, glow);
            CursorFill.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(Hex(down ? "#BDBDBD" : "#FFFFFF"), time) { EasingFunction = ease });
        }

        protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseLeftButtonDown(e);
            PressCursor(true);
        }

        protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseLeftButtonUp(e);
            PressCursor(false);
        }

        protected override void OnPreviewMouseMove(MouseEventArgs e)
        {
            base.OnPreviewMouseMove(e);
            if (_cursorReady) MoveCursorTo(e.GetPosition(CursorLayer));
            // the button may have been released outside the window
            if (_pressed && e.LeftButton == MouseButtonState.Released) PressCursor(false);
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            ShowCursor(false);
        }

        // ------------------------------------------------------------- hint
        DispatcherTimer _hintTimer;
        FrameworkElement _hintFor;

        internal void QueueHint(FrameworkElement target)
        {
            _hintFor = target;
            if (_hintTimer == null)
            {
                _hintTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _hintTimer.Tick += (s, e) =>
                {
                    _hintTimer.Stop();
                    if (_hintFor != null && _hintFor.IsMouseOver) ShowHint(_hintFor);
                };
            }
            _hintTimer.Stop();
            _hintTimer.Start();
        }

        internal void HideHint()
        {
            _hintFor = null;
            if (_hintTimer != null) _hintTimer.Stop();
            if (HintLayer.Visibility != Visibility.Visible) return;

            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(100));
            fade.Completed += (s, e) => { if (_hintFor == null) HintLayer.Visibility = Visibility.Collapsed; };
            HintLayer.BeginAnimation(OpacityProperty, fade);
        }

        void ShowHint(FrameworkElement target)
        {
            HintText.Text = Hint.GetText(target);
            HintBox.Margin = new Thickness();
            HintLayer.Visibility = Visibility.Visible;
            HintLayer.UpdateLayout();

            // Centred on the element, but clear of the whole panel it sits in, so the hint never covers
            // the neighbouring buttons; kept inside the window, and below the panel if there is no room above.
            const double gap = 10, edge = 12;
            FrameworkElement panel = target;
            for (var p = VisualTreeHelper.GetParent(target); p != null && p != Shell; p = VisualTreeHelper.GetParent(p))
            {
                var border = p as System.Windows.Controls.Border;
                if (border != null && !double.IsNaN(Glass.GetRadius(border))) { panel = border; break; }
            }
            var over = target.TransformToAncestor(Shell).TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight));
            var anchor = panel.TransformToAncestor(Shell).TransformBounds(new Rect(0, 0, panel.ActualWidth, panel.ActualHeight));
            double w = HintBox.ActualWidth, h = HintBox.ActualHeight;
            double x = Math.Round(Math.Max(edge, Math.Min(Shell.ActualWidth - w - edge, over.Left + over.Width / 2 - w / 2)));
            double y = Math.Round(anchor.Top - gap - h);
            if (y < edge) y = Math.Round(anchor.Bottom + gap);

            HintBox.Margin = HintShadow.Margin = new Thickness(x, y, 0, 0);
            HintShadow.Width = w;
            HintShadow.Height = h;
            HintFrost.Clip = new RectangleGeometry(new Rect(x, y, w, h), 14, 14);
            HintLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
        }

        // -------------------------------------------------------------- sky
        const double SkyW = 1200, SkyH = 840;   // one star tile; taller than the window
        readonly List<Tuple<TranslateTransform, double>> _drift = new List<Tuple<TranslateTransform, double>>();
        readonly List<Tuple<List<Tuple<Point, double, int>>, TranslateTransform>> _starLayers =
            new List<Tuple<List<Tuple<Point, double, int>>, TranslateTransform>>();
        readonly List<FrameworkElement> _glass = new List<FrameworkElement>();
        string _frostKey;
        bool _drifting;

        // Three tiled star layers. Each is drawn twice from the same stars and moved by the same transform:
        // sharp for the sky, and fat for the copy behind the glass, so the heavy blur leaves soft glows
        // drifting under the panels instead of wiping the stars out.
        const double FrostGrow = 10;     // at the blur the glass was drawn for; less blur, less fattening
        const double FrostGlow = 0.24;   // how bright those glows are
        static readonly byte[] StarAlpha = { 90, 160, 255 };

        void BuildSky()
        {
            var rnd = new Random(7);
            var layers = new[]
            {
                new { Count = 320, Min = 0.35, Max = 0.7, Seconds = 520.0 },
                new { Count = 120, Min = 0.6, Max = 1.0, Seconds = 320.0 },
                new { Count = 40, Min = 0.9, Max = 1.5, Seconds = 200.0 },
            };

            foreach (var l in layers)
            {
                var stars = new List<Tuple<Point, double, int>>();
                for (int i = 0; i < l.Count; i++)
                {
                    stars.Add(Tuple.Create(
                        new Point(rnd.NextDouble() * SkyW, rnd.NextDouble() * SkyH),
                        l.Min + rnd.NextDouble() * (l.Max - l.Min),
                        rnd.Next(StarAlpha.Length)));
                }

                var move = new TranslateTransform();
                // The sharp layer is a cached bitmap two tiles wide that slides across the window. Sliding a
                // bitmap costs nothing; re-drawing hundreds of stars on every step made each step drop a frame,
                // which showed as smeared text whenever the list was scrolling.
                Sky.Children.Add(new System.Windows.Shapes.Rectangle
                {
                    Width = SkyW * 2,
                    Height = SkyH,
                    Fill = StarBrush(stars, 1, 1, Transform.Identity),
                    CacheMode = new BitmapCache(),
                    RenderTransform = move
                });
                _starLayers.Add(Tuple.Create(stars, move));
                _drift.Add(Tuple.Create(move, l.Seconds));
            }
        }

        // The copy of the sky under the glass. The stars are fattened in step with the blur: at the designed
        // strength a star spreads into a soft glow, with no blur at all it is the very star seen through clear glass.
        void BuildFrostSky()
        {
            double strength = Math.Min(1, _frostBlur.Radius / DesignBlur);
            FrostSky.Children.Clear();
            HintSky.Children.Clear();
            foreach (var l in _starLayers)
            {
                var glow = StarBrush(l.Item1, 1 + (FrostGrow - 1) * strength, 1 + (FrostGlow - 1) * strength, l.Item2);
                FrostSky.Children.Add(new System.Windows.Shapes.Rectangle { Fill = glow });
                HintSky.Children.Add(new System.Windows.Shapes.Rectangle { Fill = glow });
            }
        }

        static Brush StarBrush(List<Tuple<Point, double, int>> stars, double grow, double opacity, Transform move)
        {
            var groups = StarAlpha.Select(a => new GeometryGroup { FillRule = FillRule.Nonzero }).ToArray();
            foreach (var s in stars)
            {
                double r = s.Item2 * grow;
                // a star that crosses the tile edge is repeated on the other side, so the tiling has no seams
                foreach (double dx in new[] { -SkyW, 0, SkyW })
                {
                    foreach (double dy in new[] { -SkyH, 0, SkyH })
                    {
                        var at = new Point(s.Item1.X + dx, s.Item1.Y + dy);
                        if (at.X + r < 0 || at.X - r > SkyW || at.Y + r < 0 || at.Y - r > SkyH) continue;
                        groups[s.Item3].Children.Add(new EllipseGeometry(at, r, r));
                    }
                }
            }

            var tile = new DrawingGroup();
            tile.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, SkyW, SkyH))));
            for (int k = 0; k < groups.Length; k++)
                tile.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(StarAlpha[k], 255, 255, 255)), null, groups[k]));
            tile.Freeze();

            return new DrawingBrush(tile)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, SkyW, SkyH),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, SkyW, SkyH),
                ViewboxUnits = BrushMappingMode.Absolute,
                Opacity = opacity,
                Transform = move
            };
        }

        // The window lives in the tray most of the time, so the sky only moves while it is on screen -
        // and not at all behind a picture of the user's own.
        void UpdateDrift()
        {
            Drift(IsLoaded && IsVisible && WindowState != WindowState.Minimized && !_wallpaperOn);
        }

        void Drift(bool on)
        {
            if (on == _drifting) return;
            _drifting = on;
            foreach (var d in _drift)
            {
                DoubleAnimation a = null;
                if (on)
                {
                    a = new DoubleAnimation(0, -SkyW, TimeSpan.FromSeconds(d.Item2)) { RepeatBehavior = RepeatBehavior.Forever };
                    Timeline.SetDesiredFrameRate(a, 30);
                }
                d.Item1.BeginAnimation(TranslateTransform.XProperty, a);
            }
        }

        void CollectGlass(DependencyObject root)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var fe = child as FrameworkElement;
                if (fe != null && !double.IsNaN(Glass.GetRadius(fe))) _glass.Add(fe);
                if (!(child is System.Windows.Controls.ListBox)) CollectGlass(child);
            }
        }

        // Cuts the blurred sky down to the shapes of the glass elements.
        void UpdateFrost()
        {
            if (_glass.Count == 0) return;

            var shapes = new GeometryGroup { FillRule = FillRule.Nonzero };
            var key = new System.Text.StringBuilder().Append(Shell.ActualWidth).Append('x').Append(Shell.ActualHeight);
            foreach (var fe in _glass)
            {
                if (!fe.IsVisible || fe.ActualWidth <= 0 || fe.ActualHeight <= 0) continue;
                Rect r;
                try { r = fe.TransformToAncestor(Shell).TransformBounds(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight)); }
                catch (InvalidOperationException) { continue; }

                // glass sliding out of a clipped parent (the footer tray) is frosted only where it shows
                for (var p = VisualTreeHelper.GetParent(fe); p != null && p != Shell; p = VisualTreeHelper.GetParent(p))
                {
                    var box = p as FrameworkElement;
                    if (box != null && box.ClipToBounds)
                        r.Intersect(box.TransformToAncestor(Shell).TransformBounds(new Rect(0, 0, box.ActualWidth, box.ActualHeight)));
                }
                if (r.IsEmpty || r.Width < 1 || r.Height < 1) continue;

                double radius = Math.Min(Glass.GetRadius(fe), Math.Min(r.Width, r.Height) / 2);
                shapes.Children.Add(new RectangleGeometry(r, radius, radius));
                key.Append('|').Append(r).Append('/').Append(radius);
            }

            if (key.ToString() == _frostKey) return;
            _frostKey = key.ToString();
            shapes.Freeze();
            Frost.Clip = shapes;
        }

        // ---------------------------------------------------------- startup
        public async void Initialize()
        {
            _runner.Crashed += () => Dispatcher.BeginInvoke(new Action(OnCrashed));

            GameToggle.IsChecked = _s.GameFilter;
            AutostartToggle.IsChecked = _s.Autostart;
            CloseToggle.IsChecked = _s.CloseToTray;
            InitProxy();
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

            try
            {
                if (_s.ProxyAuto && TgProxy.IsInstalled) await ProxyStartAsync(false);
            }
            catch (Exception ex) { Log("Ошибка: " + ex.Message); }
        }

        void SetupTray()
        {
            _tray = new System.Windows.Forms.NotifyIcon
            {
                Text = "Zapret Launcher",
                Visible = true
            };
            _tray.MouseClick += (s, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowFromTray();
                else if (e.Button == System.Windows.Forms.MouseButtons.Right) ShowTrayMenu();
            };
        }

        void ShowTrayMenu()
        {
            var menu = new TrayMenu();
            menu.Add("Открыть", true, ShowFromTray);
            menu.Add(_state == UiState.Running ? "Zapret: выключить" : "Zapret: включить",
                _state == UiState.Idle || _state == UiState.Running || _state == UiState.Missing,
                () => Power_Click(null, null));
            menu.Add(_pxState == ProxyState.Running ? "Прокси Telegram: выключить" : "Прокси Telegram: включить",
                _pxState == ProxyState.Idle || _pxState == ProxyState.Running,
                () => PxPower_Click(null, null));
            menu.AddSeparator();
            menu.Add("Выход", true, ExitApp);
            menu.Popup();
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
            Color color;
            double glow = 0;
            bool pulse = false;

            switch (st)
            {
                case UiState.Missing:
                    title = "Нужна установка"; subtitle = "Нажмите, чтобы скачать компоненты zapret";
                    color = Hex("#BDBDBD"); glow = 0.3; break;
                case UiState.Installing:
                    title = "Загрузка…"; subtitle = "Скачиваю и распаковываю zapret";
                    color = Hex("#BDBDBD"); pulse = true; break;
                case UiState.Starting:
                    title = "Запуск…"; subtitle = "Подождите секунду";
                    color = Hex("#D9D9D9"); pulse = true; break;
                case UiState.Testing:
                    title = "Подбор стратегии"; subtitle = "Нажмите, чтобы отменить";
                    color = Hex("#D9D9D9"); pulse = true; break;
                case UiState.Running:
                    title = "Работает"; subtitle = "Обход блокировок включён";
                    color = _accent; glow = 0.7; break;
                default:
                    title = "Отключено"; subtitle = "Нажмите, чтобы включить";
                    color = Hex("#8A8A8A"); break;
            }
            if (sub != null) subtitle = sub;

            StatusText.Text = title;
            SubText.Text = subtitle;
            StatusText.Foreground = new SolidColorBrush(st == UiState.Running ? color : Hex("#F2F2F2"));
            Tint(color, glow, pulse, st == UiState.Running);

            bool locked = st == UiState.Installing || st == UiState.Starting || st == UiState.Testing;
            StrategyList.IsEnabled = !locked;
            GameToggle.IsEnabled = !locked;
            AutoButton.IsEnabled = !locked && _strategies.Count > 0;
            UpdateButton.IsEnabled = !locked;
            PowerButton.IsEnabled = st != UiState.Installing && st != UiState.Starting;

            SetDot(TabZapret, st == UiState.Running);
            UpdateTray();
        }

        // One tray icon for the bypass and the Telegram proxy: lit while either is running, with both named
        // in the tooltip. The window keeps the application icon (app.ico).
        void UpdateTray()
        {
            bool zapret = _state == UiState.Running, proxy = _pxState == ProxyState.Running;
            var icon = IconFactory.Tray(zapret || proxy);
            var old = _tray.Icon;
            _tray.Icon = icon;
            if (old != null) old.Dispose();
            // a tray tooltip holds 63 characters
            _tray.Text = "Zapret: " + (zapret ? "включён" : "выключен") + " · Прокси Telegram: " + (proxy ? "включён" : "выключен");
        }

        void Tint(Color color, double glow, bool pulse, bool on)
        {
            Tint(Ring, PowerIcon, HaloIn, HaloOut, color, glow, pulse, on);
        }

        // Paints a power button. Off, there is only a hint of a glow around the black disc; while it is busy a
        // thin ring breathes; on, the ring takes the accent colour and the corona behind the disc slowly flares.
        static void Tint(System.Windows.Shapes.Shape ring, System.Windows.Shapes.Shape glyph,
            FrameworkElement haloIn, FrameworkElement haloOut, Color color, double glow, bool pulse, bool on)
        {
            var d = TimeSpan.FromMilliseconds(400);
            Color edge = on ? color : pulse ? Hex("#AAAAAA") : Hex(glow > 0 ? "#66FFFFFF" : "#33FFFFFF");
            ((SolidColorBrush)ring.Stroke).BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(edge, d));
            ring.BeginAnimation(System.Windows.Shapes.Shape.StrokeThicknessProperty, new DoubleAnimation(on ? 2 : 1, d));
            ((SolidColorBrush)glyph.Stroke).BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, d));

            if (on)
            {
                Breathe(haloIn, 0.55, 0.7, 1.05, 1600);
                Breathe(haloOut, 0.18, 0.24, 1.08, 1600);
            }
            else if (pulse)
            {
                Breathe(haloIn, 0.15, 0.4, 1, 400);
                Breathe(haloOut, 0, 0, 1, 0);
            }
            else
            {
                Breathe(haloIn, glow > 0 ? 0.18 : 0.1, 0, 1, 0);
                Breathe(haloOut, 0, 0, 1, 0);
            }
        }

        // Fades a halo to "from" and, if a period is given, keeps it swinging between "from" and "to" while
        // swelling it to "grow". The swing only starts once the fade is done, and not at all if the button
        // has been repainted in the meantime.
        static void Breathe(FrameworkElement halo, double from, double to, double grow, int halfMs)
        {
            var scale = (ScaleTransform)halo.RenderTransform;
            var token = new object();
            halo.Tag = token;

            var settle = TimeSpan.FromMilliseconds(400);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, settle));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, settle));
            var fade = new DoubleAnimation(from, settle);
            if (halfMs > 0)
            {
                fade.Completed += (s, e) =>
                {
                    if (!ReferenceEquals(halo.Tag, token)) return;
                    halo.BeginAnimation(OpacityProperty, Swing(from, to, halfMs));
                    if (grow != 1)
                    {
                        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Swing(1, grow, halfMs));
                        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Swing(1, grow, halfMs));
                    }
                };
            }
            halo.BeginAnimation(OpacityProperty, fade);
        }

        static DoubleAnimation Swing(double from, double to, int halfMs)
        {
            var swing = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(halfMs))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Timeline.SetDesiredFrameRate(swing, 30);   // a slow glow does not need the monitor's full rate
            return swing;
        }

        DispatcherTimer _logTimer;
        bool _logShown, _trayOpen;

        // The footer normally shows the version line alone. A message lifts that line, fades in under it
        // and leaves again after a while.
        void Log(string message)
        {
            bool show = !string.IsNullOrEmpty(message);
            if (show) LogText.Text = message;

            var time = TimeSpan.FromMilliseconds(280);
            VersionShift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(show ? -10 : 0, time) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            LogButton.BeginAnimation(OpacityProperty,
                show ? new DoubleAnimation(_logShown ? 0.3 : 0, 1, time) : new DoubleAnimation(0, time));
            LogButton.IsHitTestVisible = _logShown = show;

            if (_logTimer == null)
            {
                _logTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                _logTimer.Tick += (s, e) => Log(null);
            }
            _logTimer.Stop();
            if (show) _logTimer.Start();
        }

        // Update / Folder live in a tray that slides out of the version line.
        void Tray_Click(object sender, RoutedEventArgs e)
        {
            _trayOpen = !_trayOpen;
            var time = TimeSpan.FromMilliseconds(_trayOpen ? 320 : 220);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            TrayClip.BeginAnimation(WidthProperty, new DoubleAnimation(_trayOpen ? Tray.ActualWidth : 0, time) { EasingFunction = ease });
            Tray.BeginAnimation(System.Windows.Controls.Canvas.LeftProperty, new DoubleAnimation(_trayOpen ? 0 : -18, time) { EasingFunction = ease });
            Tray.BeginAnimation(OpacityProperty, new DoubleAnimation(_trayOpen ? 1 : 0, time));
            TrayArrow.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(_trayOpen ? 180 : 0, time) { EasingFunction = ease });
        }

        void RefreshVersion()
        {
            // the footer belongs to whichever tab is open
            VersionText.Text = _tab == Tab.Proxy
                ? "TG WS Proxy: " + (TgProxy.InstalledVersion ?? (TgProxy.IsInstalled ? "установлен" : "не установлен"))
                : _tab == Tab.Settings ? Installer.AppName + " " + Installer.Version
                : "Компоненты zapret: " + (Engine.InstalledVersion ?? "не установлены");

            // Update / Folder are about the components; the Settings tab has none
            bool tray = _tab != Tab.Settings;
            if (!tray && _trayOpen) Tray_Click(null, null);
            TrayToggle.IsHitTestVisible = tray;
            TrayChevron.Visibility = tray ? Visibility.Visible : Visibility.Collapsed;
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

        // winws prints a version banner first and the actual error last, so the reason is the last line.
        static string Reason(string text, string fallback)
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            var lines = text.Split('\n').Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("github version", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (lines.Length == 0) return fallback;
            string last = lines[lines.Length - 1];
            return last.Length > 200 ? last.Substring(0, 200) + "…" : last;
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
                    throw new Exception(Reason(_runner.LastOutput, "winws.exe сразу завершился"));

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
            Log("winws.exe завершился: " + Reason(_runner.LastOutput, "неизвестная причина"));
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
            foreach (var s in _strategies) s.Works = false;
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

        void CloseToTray_Click(object sender, RoutedEventArgs e)
        {
            _s.CloseToTray = CloseToggle.IsChecked == true;
            _s.Save();
        }

        // Tries every strategy in turn and marks each one that works in the list, instead of stopping at
        // the first. When it is done, the first working one is switched on; any other marked row can be picked.
        async void Auto_Click(object sender, RoutedEventArgs e)
        {
            if (_strategies.Count == 0) return;

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            var working = new List<Strategy>();
            foreach (var s in _strategies) s.Works = false;

            SetState(UiState.Testing);
            try
            {
                if (_runner.IsRunning) await Task.Run(() => _runner.Stop());

                for (int i = 0; i < _strategies.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var st = _strategies[i];
                    Log("Проверка " + (i + 1) + " из " + _strategies.Count + ": " + st.Name
                        + (working.Count > 0 ? " · рабочих: " + working.Count : ""));
                    SubText.Text = st.Name;
                    StrategyList.ScrollIntoView(st);

                    // Start() stops whatever the previous round left running
                    try { await Task.Run(() => _runner.Start(st, _s.GameFilter), ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { continue; }

                    await Task.Delay(1800, ct);
                    if (!_runner.IsRunning) continue;

                    if (await Probe.OkAsync(ct))
                    {
                        st.Works = true;
                        working.Add(st);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log("Ошибка подбора: " + ex.Message); }

            await Task.Run(() => _runner.Stop());
            bool cancelled = ct.IsCancellationRequested;

            if (working.Count > 0 && !cancelled)
            {
                var first = working[0];
                _suppress = true;
                StrategyList.SelectedItem = first;
                _suppress = false;
                StrategyList.ScrollIntoView(first);
                _s.Strategy = first.Name;
                _s.Save();

                await StartAsync();
                if (_state == UiState.Running)
                    Log("Рабочих стратегий: " + working.Count + " из " + _strategies.Count + " — отмечены в списке. Включена " + first.Name);
            }
            else
            {
                SetState(UiState.Idle);
                Log(working.Count > 0 ? "Подбор остановлен. Рабочих найдено: " + working.Count + " — отмечены в списке"
                    : cancelled ? "Подбор отменён"
                    : "Рабочая стратегия не найдена — попробуйте вручную или обновите компоненты");
            }
        }

        async void Update_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_tab == Tab.Proxy) await ProxyInstallAsync(true);
                else await InstallAsync(true);
            }
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
            if (e.ChangedButton != MouseButton.Left) return;
            DragMove();
            PressCursor(false);   // the move loop swallows the button-up
        }

        void Min_Click(object sender, RoutedEventArgs e) { WindowState = WindowState.Minimized; }

        void Close_Click(object sender, RoutedEventArgs e) { Close(); }

        // The list scrolls by pixels; the wheel eases towards a target offset instead of jumping.
        static readonly DependencyProperty ListOffsetProperty = DependencyProperty.Register(
            "ListOffset", typeof(double), typeof(MainWindow), new PropertyMetadata(0.0, (d, e) =>
            {
                var w = (MainWindow)d;
                if (w._listScroll != null) w._listScroll.ScrollToVerticalOffset(w.SnapToPixel((double)e.NewValue));
            }));

        // Text drawn between device pixels is what smeared while the list was moving.
        double SnapToPixel(double v) { return Math.Round(v * _dpiScale) / _dpiScale; }

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
            _scrollTarget = SnapToPixel(Math.Max(0, Math.Min(_listScroll.ScrollableHeight, _scrollTarget - e.Delta * 0.8)));

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
                if (_tab == Tab.Proxy)
                {
                    ProxyFolder();
                    return;
                }
                string dir = Engine.FindRoot() ?? Paths.Root;
                System.IO.Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex) { Log(ex.Message); }
        }

        void PxFolder_Click(object sender, RoutedEventArgs e) { Folder_Click(sender, e); }

        void UserList_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = Engine.EnsureUserLists();
                if (dir == null) { Log("Сначала установите компоненты zapret"); return; }
                string file = (string)((FrameworkElement)sender).Tag;
                Process.Start("notepad.exe", "\"" + System.IO.Path.Combine(dir, file) + "\"");
            }
            catch (Exception ex) { Log(ex.Message); }
        }

        void ListsFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = Engine.EnsureUserLists();
                if (dir == null) { Log("Сначала установите компоненты zapret"); return; }
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex) { Log(ex.Message); }
        }

        // -------------------------------------------------------- closing
        protected override void OnClosing(CancelEventArgs e)
        {
            if (_setupBusy) { e.Cancel = true; return; }   // not in the middle of copying files

            // With "close to tray" on, the close button only hides the window; Exit in the tray menu quits.
            if (!_exiting && _s.CloseToTray && _setup == SetupMode.None)
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
            if (_setup != SetupMode.None) { SetupCleanup(); return; }   // the wizard must not stop a running bypass
            try { _tray.Visible = false; _tray.Dispose(); } catch { }
            _runner.Stop();
            _proxy.Stop();
        }

        void ExitApp()
        {
            _exiting = true;
            Close();
        }
    }
}
