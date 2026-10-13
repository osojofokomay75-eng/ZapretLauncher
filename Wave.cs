using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace ZapretLauncher
{
    /// <summary>
    /// A ring that travels outwards and bends the picture as it passes (wave.fx). Put on an element, it
    /// distorts everything the element draws.
    /// </summary>
    sealed class WaveEffect : ShaderEffect
    {
        static readonly PixelShader Shader = Load();

        static PixelShader Load()
        {
            var shader = new PixelShader();
            using (var packed = typeof(WaveEffect).Assembly.GetManifestResourceStream("wave.ps"))
            {
                var copy = new MemoryStream();   // not disposed: the shader may read it later
                packed.CopyTo(copy);
                copy.Position = 0;
                shader.SetStreamSource(copy);
            }
            return shader;
        }

        public static readonly DependencyProperty InputProperty =
            RegisterPixelShaderSamplerProperty("Input", typeof(WaveEffect), 0);
        public static readonly DependencyProperty CentreProperty = DependencyProperty.Register(
            "Centre", typeof(Point), typeof(WaveEffect), new UIPropertyMetadata(new Point(0.5, 0.5), PixelShaderConstantCallback(0)));
        public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
            "Radius", typeof(double), typeof(WaveEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(1)));
        public static readonly DependencyProperty AmountProperty = DependencyProperty.Register(
            "Amount", typeof(double), typeof(WaveEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(2)));
        public static readonly DependencyProperty BreadthProperty = DependencyProperty.Register(
            "Breadth", typeof(double), typeof(WaveEffect), new UIPropertyMetadata(0.1, PixelShaderConstantCallback(3)));
        public static readonly DependencyProperty AspectProperty = DependencyProperty.Register(
            "Aspect", typeof(double), typeof(WaveEffect), new UIPropertyMetadata(1.0, PixelShaderConstantCallback(4)));

        public WaveEffect()
        {
            PixelShader = Shader;
            UpdateShaderValue(InputProperty);
            UpdateShaderValue(CentreProperty);
            UpdateShaderValue(RadiusProperty);
            UpdateShaderValue(AmountProperty);
            UpdateShaderValue(BreadthProperty);
            UpdateShaderValue(AspectProperty);
        }

        public Brush Input { get { return (Brush)GetValue(InputProperty); } set { SetValue(InputProperty, value); } }
        public Point Centre { get { return (Point)GetValue(CentreProperty); } set { SetValue(CentreProperty, value); } }
        public double Radius { get { return (double)GetValue(RadiusProperty); } set { SetValue(RadiusProperty, value); } }
        public double Amount { get { return (double)GetValue(AmountProperty); } set { SetValue(AmountProperty, value); } }
        public double Breadth { get { return (double)GetValue(BreadthProperty); } set { SetValue(BreadthProperty, value); } }
        public double Aspect { get { return (double)GetValue(AspectProperty); } set { SetValue(AspectProperty, value); } }
    }

    public partial class MainWindow
    {
        const double WaveShift = 0.05;      // the shift at full strength, in window heights
        const double WaveBreadth = 0.2;     // half the breadth of the front, in window heights: a long, gentle swell
        const int WaveMs = 1800;

        DispatcherTimer _wavePreview;

        /// <summary>
        /// Sends a ripple across the whole window from the middle of the given element - what switching the
        /// bypass or the proxy on looks like. The effect sits on the window only while the ripple travels.
        /// </summary>
        void PlayWave(FrameworkElement from)
        {
            if (_s.Wave <= 0 || _setup != SetupMode.None) return;
            if (!IsLoaded || !IsVisible || WindowState == WindowState.Minimized) return;
            double w = Shell.ActualWidth, h = Shell.ActualHeight;
            if (w <= 0 || h <= 0 || from.ActualWidth <= 0) return;

            Point at;
            try { at = from.TransformToAncestor(Shell).Transform(new Point(from.ActualWidth / 2, from.ActualHeight / 2)); }
            catch (InvalidOperationException) { return; }   // not on screen

            // far enough for the front to leave through the farthest corner
            double dx = Math.Max(at.X, w - at.X) / h, dy = Math.Max(at.Y, h - at.Y) / h;
            double reach = Math.Sqrt(dx * dx + dy * dy) + WaveBreadth * 2.5;

            var wave = new WaveEffect { Centre = new Point(at.X / w, at.Y / h), Aspect = w / h, Breadth = WaveBreadth };
            var time = TimeSpan.FromMilliseconds(WaveMs);
            var travel = new DoubleAnimation(0, reach, time) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } };
            travel.Completed += (s, e) => { if (ReferenceEquals(Shell.Effect, wave)) Shell.Effect = null; };
            // it swells up rather than starting at full height, and dies away as it spreads
            var fade = new DoubleAnimationUsingKeyFrames { Duration = time };
            fade.KeyFrames.Add(new EasingDoubleKeyFrame(WaveShift * _s.Wave / 100.0, KeyTime.FromPercent(0.22), new SineEase { EasingMode = EasingMode.EaseOut }));
            fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), new SineEase { EasingMode = EasingMode.EaseInOut }));

            Shell.Effect = wave;
            wave.BeginAnimation(WaveEffect.AmountProperty, fade);
            wave.BeginAnimation(WaveEffect.RadiusProperty, travel);
        }

        // the ripple starts at the power button of the tab on screen, or at the tab's name if another tab is open
        void WaveFor(Tab tab)
        {
            if (tab == Tab.Proxy) PlayWave(_shown == Tab.Proxy ? (FrameworkElement)PxDisc : TabProxy);
            else PlayWave(_shown == Tab.Zapret ? (FrameworkElement)Disc : TabZapret);
        }

        void Wave_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_lookReady) return;
            int value = (int)Math.Round(e.NewValue);
            if (value == _s.Wave) return;
            _s.Wave = value;
            ShowWave();
            SaveSoon();

            // once the slider comes to rest, a ripple from it shows what was chosen
            if (_wavePreview == null)
            {
                _wavePreview = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                _wavePreview.Tick += (s, ev) =>
                {
                    _wavePreview.Stop();
                    PlayWave(WaveSlider);
                };
            }
            _wavePreview.Stop();
            _wavePreview.Start();
        }

        void ShowWave() { WaveValue.Text = _s.Wave <= 0 ? "выкл" : _s.Wave + "%"; }
    }
}
