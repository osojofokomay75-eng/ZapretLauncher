using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ZapretLauncher
{
    /// <summary>
    /// The Settings tab and the looks it controls: the picture behind everything, how much the glass blurs
    /// it, and the accent colour. The switches for the bypass and the proxy sit on this tab too, but their
    /// handlers stay with the code they belong to.
    /// </summary>
    public partial class MainWindow
    {
        const double BlurPerPercent = 1.2;   // the slider's default, 60%, is the radius the glass was drawn for (72)
        const double DesignBlur = 72;
        const double Bleed = 130;            // how far the blurred picture reaches past the window; see MainWindow.xaml

        readonly BlurEffect _frostBlur = new BlurEffect { RenderingBias = RenderingBias.Performance };
        Color _accent = Colors.White;
        bool _wallpaperOn, _lookReady;
        DispatcherTimer _saveTimer;

        /// <summary>Puts the saved looks on the window. Called once, from the constructor.</summary>
        void InitLook()
        {
            FrostSky.Effect = HintSky.Effect = FrostWallpaperBox.Effect = HintWallpaperBox.Effect = _frostBlur;
            Shell.SizeChanged += (s, e) => FitWallpaper();

            ApplyAccent(ParseColor(_s.Accent) ?? Colors.White);
            ApplyWallpaper();
            ApplyBlur();
            BlurSlider.Value = _s.Blur;
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

        static BitmapSource LoadPicture(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
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
        }

        /// <summary>Shows the saved picture, or the stars when there is none or it will not load.</summary>
        void ApplyWallpaper()
        {
            BitmapSource picture = null;
            if (!string.IsNullOrEmpty(_s.Background))
            {
                try { picture = LoadPicture(_s.Background); }
                catch { }
            }
            _wallpaperOn = picture != null;

            if (_wallpaperOn)
            {
                WallpaperBox.Fill = new ImageBrush(picture) { Stretch = Stretch.UniformToFill };
                // The copy under the glass is the same picture, laid out the same way, but repeated past the
                // window's edges as mirror images: a blur fades towards whatever lies outside the picture.
                var under = new ImageBrush(picture)
                {
                    Stretch = Stretch.UniformToFill,
                    TileMode = TileMode.FlipXY,
                    ViewportUnits = BrushMappingMode.Absolute
                };
                FrostWallpaperBox.Fill = HintWallpaperBox.Fill = under;
                FitWallpaper();
            }
            else
            {
                WallpaperBox.Fill = FrostWallpaperBox.Fill = HintWallpaperBox.Fill = null;
            }

            var shown = _wallpaperOn ? Visibility.Visible : Visibility.Collapsed;
            var hidden = _wallpaperOn ? Visibility.Collapsed : Visibility.Visible;
            Wallpaper.Visibility = FrostWallpaper.Visibility = HintWallpaper.Visibility = shown;
            Sky.Visibility = FrostSkyHost.Visibility = HintSkyHost.Visibility = hidden;
            UpdateDrift();

            BgName.Text = _wallpaperOn ? "Своя картинка" : "Звёзды";
            BgReset.Visibility = shown;
        }

        // the tile of the picture under the glass has to sit exactly where the window is
        void FitWallpaper()
        {
            var under = FrostWallpaperBox.Fill as ImageBrush;
            if (under == null || Shell.ActualWidth <= 0) return;
            under.Viewport = new Rect(Bleed, Bleed, Shell.ActualWidth, Shell.ActualHeight);
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

                _s.Background = copy;
                _s.Save();
                ApplyWallpaper();
                Log("Фон изменён");
            }
            catch (Exception)
            {
                Log("Не удалось открыть этот файл как картинку");
            }
        }

        void BgReset_Click(object sender, RoutedEventArgs e)
        {
            _s.Background = "";
            _s.Save();
            ApplyWallpaper();
            foreach (var old in SavedPictures())
            {
                try { File.Delete(old); } catch { }
            }
            Log("Фон: звёзды");
        }

        // ------------------------------------------------------------- blur
        /// <summary>Sets how strongly the glass blurs what is under it, from the saved 0..100.</summary>
        void ApplyBlur()
        {
            double radius = _s.Blur * BlurPerPercent;
            _frostBlur.Radius = radius;

            // The copies under the glass are drawn small and stretched: a heavy blur hides that and costs a
            // fraction. A light blur would show the pixels, so the lighter it is, the closer to full size.
            double scale = Math.Min(1, Math.Max(0.25, 18 / Math.Max(radius, 1)));
            foreach (var host in new[] { FrostSkyHost, HintSkyHost, FrostWallpaperHost, HintWallpaperHost })
                host.CacheMode = new BitmapCache(scale);

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
        static Color? ParseColor(string text)
        {
            var m = Regex.Match((text ?? "").Trim(), "^#?([0-9a-fA-F]{6})$");
            if (!m.Success) return null;
            int rgb = Convert.ToInt32(m.Groups[1].Value, 16);
            return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        static string HexOf(Color c) { return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }

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

            // the power buttons are coloured from code
            if (IsLoaded && _setup == SetupMode.None)
            {
                SetState(_state, SubText.Text);
                SetProxyState(_pxState);
            }
        }

        static Brush Frozen(Brush brush)
        {
            brush.Freeze();
            return brush;
        }

        // marks the swatch of the current accent, if it is one of them, and writes it out in the field
        void ShowAccent()
        {
            bool ready = _lookReady;
            _lookReady = false;   // ticking a swatch here is not the user choosing it
            foreach (var swatch in AccentSwatches.Children.OfType<RadioButton>())
                swatch.IsChecked = ((SolidColorBrush)swatch.Background).Color == _accent;
            AccentHex.Text = HexOf(_accent);
            _lookReady = ready;
        }

        void SetAccent(Color color)
        {
            _s.Accent = HexOf(color);
            _s.Save();
            ApplyAccent(color);
            ShowAccent();
        }

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
