using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ZapretLauncher
{
    /// <summary>
    /// The Settings tab and the looks it controls: the picture behind everything, how bright and how blurred
    /// it is, how much the glass blurs it, and the accent colour. The switches for the bypass and the proxy
    /// sit on this tab too, but their handlers stay with the code they belong to.
    /// </summary>
    public partial class MainWindow
    {
        const string Stars = "stars";        // what Settings.Background holds when the star sky is chosen
        const double BlurPerPercent = 1.2;   // the glass slider's default, 60%, is the radius the glass was drawn for (72)
        const double DesignBlur = 72;
        const double BackBlurPerPercent = 0.6;
        const double Bleed = 130;            // how far the picture reaches past the window; see MainWindow.xaml

        readonly BlurEffect _frostBlur = new BlurEffect { RenderingBias = RenderingBias.Performance };
        readonly BlurEffect _backBlur = new BlurEffect { RenderingBias = RenderingBias.Performance };
        Color _accent = Colors.White;
        bool _wallpaperOn, _lookReady;
        double _frostScale = 0.25;   // the size, against full size, at which the copies under the glass are kept
        DispatcherTimer _saveTimer;

        // Everything under a piece of glass exists three times: under the panels, under the hint and under
        // a window opened over the launcher. Each has a copy of the stars and a copy of the picture.
        Grid[] SkyCopies() { return new[] { FrostSky, HintSky, ModalSky }; }
        Grid[] SkyHosts() { return new[] { FrostSkyHost, HintSkyHost, ModalSkyHost }; }
        Grid[] PictureCopies() { return new[] { FrostWallpaper, HintWallpaper, ModalWallpaper }; }
        Grid[] PictureHosts() { return new[] { FrostWallpaperHost, HintWallpaperHost, ModalWallpaperHost }; }
        System.Windows.Shapes.Rectangle[] PictureBoxes() { return new[] { FrostWallpaperBox, HintWallpaperBox, ModalWallpaperBox }; }
        System.Windows.Shapes.Rectangle[] PictureDims() { return new[] { FrostWallpaperDim, HintWallpaperDim, ModalWallpaperDim }; }

        /// <summary>Puts the saved looks on the window. Called once, from the constructor.</summary>
        void InitLook()
        {
            foreach (var box in PictureBoxes()) box.Effect = _frostBlur;
            Shell.SizeChanged += (s, e) => FitWallpaper();

            ApplyAccent(ParseColor(_s.Accent) ?? Colors.White);
            ApplyWallpaper();
            ApplyBlur();
            BlurSlider.Value = _s.Blur;
            BrightSlider.Value = _s.BackBright;
            BgBlurSlider.Value = _s.BackBlur;
            WaveSlider.Value = _s.Wave;
            ShowWave();
            ShowAccent();
            _lookReady = true;
        }

        // Settings change in bursts - a slider being dragged - so they are written once things go quiet.
        void SaveSoon()
        {
            if (_saveTimer == null)
            {
                _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                _saveTimer.Tick += (s, e) =>
                {
                    _saveTimer.Stop();
                    _s.Save();
                };
            }
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        // ------------------------------------------------------- background
        static string[] SavedPictures()
        {
            try { return Directory.GetFiles(Paths.Root, "background.*"); }
            catch { return new string[0]; }
        }

        static BitmapSource LoadPicture(Stream file)
        {
            // a photo can be many times the window; decoding it whole would cost memory for nothing
            int width = BitmapFrame.Create(file, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).PixelWidth;
            file.Position = 0;

            var picture = new BitmapImage();
            picture.BeginInit();
            picture.CacheOption = BitmapCacheOption.OnLoad;   // read now, so the file is not held open
            picture.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            picture.StreamSource = file;
            if (width > 2560) picture.DecodePixelWidth = 2560;
            picture.EndInit();
            picture.Freeze();
            return picture;
        }

        static BitmapSource LoadPicture(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) return LoadPicture(file);
        }

        // the picture the launcher comes with
        static BitmapSource OwnPicture()
        {
            using (var packed = typeof(MainWindow).Assembly.GetManifestResourceStream("background.jpg"))
            using (var copy = new MemoryStream())
            {
                packed.CopyTo(copy);   // the decoder wants a stream it can rewind
                copy.Position = 0;
                return LoadPicture(copy);
            }
        }

        /// <summary>
        /// Shows the chosen backdrop: the stars, the user's picture, or the launcher's own - which also stands
        /// in for a picture that will not load.
        /// </summary>
        void ApplyWallpaper()
        {
            BitmapSource picture = null;
            bool own = false;
            if (_s.Background != Stars)
            {
                if (!string.IsNullOrEmpty(_s.Background))
                {
                    try { picture = LoadPicture(_s.Background); }
                    catch { }
                }
                if (picture == null)
                {
                    try
                    {
                        picture = OwnPicture();
                        own = true;
                    }
                    catch { }
                }
            }
            _wallpaperOn = picture != null;

            ImageBrush tiled = null;
            if (_wallpaperOn)
            {
                // Every copy is the same picture laid out the same way and repeated past the window's edges
                // as mirror images: a blur fades towards whatever lies outside the picture.
                tiled = new ImageBrush(picture)
                {
                    Stretch = Stretch.UniformToFill,
                    TileMode = TileMode.FlipXY,
                    ViewportUnits = BrushMappingMode.Absolute
                };
            }
            WallpaperBox.Fill = tiled;
            foreach (var box in PictureBoxes()) box.Fill = tiled;
            FitWallpaper();

            var shown = _wallpaperOn ? Visibility.Visible : Visibility.Collapsed;
            var hidden = _wallpaperOn ? Visibility.Collapsed : Visibility.Visible;
            Wallpaper.Visibility = shown;
            foreach (var copy in PictureCopies()) copy.Visibility = shown;
            Sky.Visibility = hidden;
            foreach (var host in SkyHosts()) host.Visibility = hidden;
            UpdateDrift();

            BgName.Text = !_wallpaperOn ? "Звёзды" : own ? "Стандартный" : "Своя картинка";
            // brightness and blur are the picture's; the stars have neither
            BrightSlider.IsEnabled = BgBlurSlider.IsEnabled = _wallpaperOn;
            BrightSlider.Opacity = BgBlurSlider.Opacity = BrightValue.Opacity = BgBlurValue.Opacity = _wallpaperOn ? 1 : 0.4;
            ApplyBackdrop();
        }

        // the tile of the picture has to sit exactly where the window is
        void FitWallpaper()
        {
            var tiled = WallpaperBox.Fill as ImageBrush;
            if (tiled == null || Shell.ActualWidth <= 0) return;
            tiled.Viewport = new Rect(Bleed, Bleed, Shell.ActualWidth, Shell.ActualHeight);
        }

        /// <summary>The picture's brightness and its own blur, from the saved 0..100 each.</summary>
        void ApplyBackdrop()
        {
            // less bright is a darker veil over the picture; under the glass it is a shade darker still
            double dim = 1 - _s.BackBright / 100.0;
            WallpaperDim.Fill = Frozen(new SolidColorBrush(Color.FromArgb((byte)(dim * 255), 0, 0, 0)));
            var under = Frozen(new SolidColorBrush(Color.FromArgb((byte)(Math.Min(0.92, dim + 0.14) * 255), 0, 0, 0)));
            foreach (var veil in PictureDims()) veil.Fill = under;

            double radius = _s.BackBlur * BackBlurPerPercent;
            _backBlur.Radius = radius;
            WallpaperBox.Effect = radius < 1 ? null : _backBlur;
            // The picture is always kept as a bitmap: drawn straight from its brush - scaled, tiled and
            // mirrored - it is rasterised in software, over again for every frame anything above it moves.
            // Sharp, the bitmap is full size; blurred, small and stretched like the copies under the glass.
            WallpaperHost.CacheMode = new BitmapCache(radius < 1 ? 1 : Math.Min(1, Math.Max(0.25, 18 / radius)));

            BrightValue.Text = _s.BackBright + "%";
            BgBlurValue.Text = _s.BackBlur + "%";
            ApplyBlur();
        }

        void SetBackground(string value, string said)
        {
            _s.Background = value;
            _s.Save();
            ApplyWallpaper();
            // the launcher's copy of a picture no longer used is not kept
            if (value == "" || value == Stars)
            {
                foreach (var old in SavedPictures())
                {
                    try { File.Delete(old); } catch { }
                }
            }
            Log(said);
        }

        void BgPick_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Картинка для фона",
                Filter = "Картинки|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|Все файлы|*.*"
            };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                LoadPicture(dialog.FileName);   // not a picture: say so and change nothing

                // the launcher keeps a copy of its own, so the original may be moved or deleted
                Directory.CreateDirectory(Paths.Root);
                string copy = Path.Combine(Paths.Root, "background" + Path.GetExtension(dialog.FileName).ToLowerInvariant());
                if (!string.Equals(Path.GetFullPath(dialog.FileName), copy, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var old in SavedPictures()) File.Delete(old);
                    File.Copy(dialog.FileName, copy, true);
                }
                SetBackground(copy, "Фон изменён");
            }
            catch (Exception)
            {
                Log("Не удалось открыть этот файл как картинку");
            }
        }

        void BgReset_Click(object sender, RoutedEventArgs e) { SetBackground("", "Фон: стандартный"); }

        void BgStars_Click(object sender, RoutedEventArgs e) { SetBackground(Stars, "Фон: звёзды"); }

        void Bright_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_lookReady) return;
            int value = (int)Math.Round(e.NewValue);
            if (value == _s.BackBright) return;
            _s.BackBright = value;
            ApplyBackdrop();
            SaveSoon();
        }

        void BgBlur_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_lookReady) return;
            int value = (int)Math.Round(e.NewValue);
            if (value == _s.BackBlur) return;
            _s.BackBlur = value;
            ApplyBackdrop();
            SaveSoon();
        }

        // ------------------------------------------------------------- blur
        /// <summary>Sets how strongly the glass blurs what is under it, from the saved 0..100.</summary>
        void ApplyBlur()
        {
            // Glass over a picture that is itself blurred must not be sharper than the picture around it:
            // the two blurs add up the way blurs do, as the root of the sum of squares.
            double glass = _s.Blur * BlurPerPercent, back = _wallpaperOn ? _backBlur.Radius : 0;
            double radius = Math.Sqrt(glass * glass + back * back);
            _frostBlur.Radius = radius;

            // The copies under the glass are drawn small and stretched: a heavy blur hides that and costs a
            // fraction. A light blur would show the pixels, so the lighter it is, the closer to full size.
            _frostScale = Math.Min(1, Math.Max(0.25, 18 / Math.Max(radius, 1)));
            foreach (var host in PictureHosts()) host.CacheMode = new BitmapCache(_frostScale);

            BuildFrostSky();
            BlurValue.Text = _s.Blur + "%";
        }

        void Blur_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_lookReady) return;
            int value = (int)Math.Round(e.NewValue);
            if (value == _s.Blur) return;
            _s.Blur = value;
            ApplyBlur();
            SaveSoon();
        }

        // ----------------------------------------------------------- accent
        // The accent as the palette shows it. Kept apart from the colour itself: black has no hue and grey no
        // saturation to read back, and the marks must not jump when a drag passes through them.
        double _hue, _sat, _val = 1;

        static Color? ParseColor(string text)
        {
            var m = Regex.Match((text ?? "").Trim(), "^#?([0-9a-fA-F]{6})$");
            if (!m.Success) return null;
            int rgb = Convert.ToInt32(m.Groups[1].Value, 16);
            return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        static string HexOf(Color c) { return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }

        /// <summary>Hue in degrees, saturation and value 0..1.</summary>
        static Color FromHsv(double hue, double sat, double val)
        {
            double c = val * sat, h = hue / 60 % 6, x = c * (1 - Math.Abs(h % 2 - 1)), m = val - c;
            double r = 0, g = 0, b = 0;
            if (h < 1) { r = c; g = x; }
            else if (h < 2) { r = x; g = c; }
            else if (h < 3) { g = c; b = x; }
            else if (h < 4) { g = x; b = c; }
            else if (h < 5) { r = x; b = c; }
            else { r = c; b = x; }
            return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }

        static void ToHsv(Color color, out double hue, out double sat, out double val)
        {
            double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), span = max - min;
            val = max;
            sat = max <= 0 ? 0 : span / max;
            if (span <= 0) hue = 0;
            else if (max == r) hue = 60 * ((g - b) / span % 6);
            else if (max == g) hue = 60 * ((b - r) / span + 2);
            else hue = 60 * ((r - g) / span + 4);
            if (hue < 0) hue += 360;
        }

        /// <summary>
        /// Repaints everything that carries the accent. The brushes are replaced rather than recoloured:
        /// styles freeze the brushes they are given, and the XAML looks these up dynamically.
        /// </summary>
        void ApplyAccent(Color color)
        {
            _accent = color;
            Resources["SignalColor"] = color;
            Resources["Signal"] = Frozen(new SolidColorBrush(color));
            // the quieter shade, for larger areas such as a switch that is on
            Resources["SignalSoft"] = Frozen(new SolidColorBrush(Color.FromRgb(
                (byte)(color.R * 0.64), (byte)(color.G * 0.64), (byte)(color.B * 0.64))));

            // what a button that is on takes from the accent without being bound to it: its ring and its caption
            if (!IsLoaded || _setup != SetupMode.None) return;
            if (_state == UiState.Running) Recolour(Ring, StatusText);
            if (_pxState == ProxyState.Running) Recolour(PxRing, PxStatus);
        }

        void Recolour(System.Windows.Shapes.Shape ring, TextBlock caption)
        {
            // an animation holds the ring's colour (see Tint), so the new one has to go in as an animation too
            ((SolidColorBrush)ring.Stroke).BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(_accent, TimeSpan.Zero));
            caption.Foreground = new SolidColorBrush(_accent);
        }

        static Brush Frozen(Brush brush)
        {
            brush.Freeze();
            return brush;
        }

        // Shows the current accent in the controls that choose it: ticks its swatch, if it is one of them,
        // writes it out in the field and, unless the palette is where it came from, moves the palette's marks.
        void ShowAccent(bool fromPalette = false)
        {
            bool ready = _lookReady;
            _lookReady = false;   // ticking a swatch here is not the user choosing it
            foreach (var swatch in AccentSwatches.Children.OfType<RadioButton>())
                swatch.IsChecked = ((SolidColorBrush)swatch.Background).Color == _accent;
            AccentHex.Text = HexOf(_accent);
            _lookReady = ready;

            if (!fromPalette)
            {
                double hue;
                ToHsv(_accent, out hue, out _sat, out _val);
                if (_sat > 0 && _val > 0) _hue = hue;   // a grey keeps the hue the palette was on
            }
            SvHue.Fill = new SolidColorBrush(FromHsv(_hue, 1, 1));
            PlaceMarks();
        }

        void PlaceMarks()
        {
            if (SvBox.ActualWidth <= 0) return;
            Canvas.SetLeft(SvMark, _sat * SvBox.ActualWidth - SvMark.Width / 2);
            Canvas.SetTop(SvMark, (1 - _val) * SvBox.ActualHeight - SvMark.Height / 2);
            Canvas.SetTop(HueMark, _hue / 360 * HueBox.ActualHeight - HueMark.Height / 2);
        }

        void Palette_SizeChanged(object sender, SizeChangedEventArgs e) { PlaceMarks(); }

        void SetAccent(Color color)
        {
            _s.Accent = HexOf(color);
            _s.Save();
            ApplyAccent(color);
            ShowAccent();
        }

        // the colour under the palette's marks becomes the accent; called on every step of a drag
        void PickAccent()
        {
            var color = FromHsv(_hue, _sat, _val);
            _s.Accent = HexOf(color);
            SaveSoon();
            ApplyAccent(color);
            ShowAccent(true);
        }

        static double Share(double at, double of) { return of <= 0 ? 0 : Math.Max(0, Math.Min(1, at / of)); }

        void SvAt(Point at)
        {
            _sat = Share(at.X, SvBox.ActualWidth);
            _val = 1 - Share(at.Y, SvBox.ActualHeight);
            PickAccent();
        }

        void HueAt(Point at)
        {
            _hue = Math.Min(359.9, Share(at.Y, HueBox.ActualHeight) * 360);
            PickAccent();
        }

        void Sv_Down(object sender, MouseButtonEventArgs e)
        {
            SvBox.CaptureMouse();   // so the drag goes on outside the square
            SvAt(e.GetPosition(SvBox));
        }

        void Sv_Move(object sender, MouseEventArgs e)
        {
            if (SvBox.IsMouseCaptured) SvAt(e.GetPosition(SvBox));
        }

        void Hue_Down(object sender, MouseButtonEventArgs e)
        {
            HueBox.CaptureMouse();
            HueAt(e.GetPosition(HueBox));
        }

        void Hue_Move(object sender, MouseEventArgs e)
        {
            if (HueBox.IsMouseCaptured) HueAt(e.GetPosition(HueBox));
        }

        void Palette_Up(object sender, MouseButtonEventArgs e) { ((UIElement)sender).ReleaseMouseCapture(); }

        void Accent_Checked(object sender, RoutedEventArgs e)
        {
            if (!_lookReady) return;
            SetAccent(((SolidColorBrush)((RadioButton)sender).Background).Color);
        }

        void AccentHexEntered()
        {
            var color = ParseColor(AccentHex.Text);
            if (color == null)
            {
                if (AccentHex.Text.Trim() != HexOf(_accent)) Log("Цвет — это шесть шестнадцатеричных цифр, например #7AB8FF");
                AccentHex.Text = HexOf(_accent);
                return;
            }
            if (color.Value != _accent) SetAccent(color.Value);
            else AccentHex.Text = HexOf(_accent);
        }

        void AccentHex_LostFocus(object sender, RoutedEventArgs e) { AccentHexEntered(); }

        void AccentHex_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            AccentHexEntered();
            e.Handled = true;
        }
    }
}
