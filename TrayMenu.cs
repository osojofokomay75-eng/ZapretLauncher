using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace ZapretLauncher
{
    /// <summary>
    /// The tray icon's right-click menu, drawn in the launcher's own style instead of the system one.
    /// It is a small borderless window that opens at the pointer and closes as soon as it loses focus.
    /// </summary>
    sealed class TrayMenu : Window
    {
        const double Shadow = 14;   // room around the panel for its shadow

        static readonly ControlTemplate ItemTemplate = (ControlTemplate)XamlReader.Parse(
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'" +
            " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
            "<Border x:Name='Bd' Background='Transparent' CornerRadius='8' Padding='12,8'><ContentPresenter /></Border>" +
            "<ControlTemplate.Triggers>" +
            "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Background' Value='#1FFFFFFF' /></Trigger>" +
            "<Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4' /></Trigger>" +
            "</ControlTemplate.Triggers></ControlTemplate>");

        readonly StackPanel _items = new StackPanel { MinWidth = 190 };
        bool _closing;

        public TrayMenu()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            UseLayoutRounding = true;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

            // the shadow sits on its own layer so the text above it stays crisp
            var shadow = new Border
            {
                Margin = new Thickness(Shadow),
                CornerRadius = new CornerRadius(12),
                Background = Brushes.Black,
                Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 0, Opacity = 0.6 }
            };
            var panel = new Border
            {
                Margin = new Thickness(Shadow),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x10, 0x10, 0x10)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6),
                Child = _items
            };
            var root = new Grid();
            root.Children.Add(shadow);
            root.Children.Add(panel);
            Content = root;

            Deactivated += (s, e) => Dismiss();
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) Dismiss(); };
        }

        public void Add(string text, bool enabled, Action action)
        {
            var item = new Button
            {
                Content = text,
                IsEnabled = enabled,
                Template = ItemTemplate,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)),
                FontSize = 13,
                Cursor = Cursors.Hand,
                Focusable = false
            };
            item.Click += (s, e) => { Dismiss(); action(); };
            _items.Children.Add(item);
        }

        public void AddSeparator()
        {
            _items.Children.Add(new Border
            {
                Height = 1,
                Margin = new Thickness(8, 5, 8, 5),
                Background = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF))
            });
        }

        /// <summary>Opens the menu with its corner at the mouse pointer, kept inside the screen's work area.</summary>
        public void Popup()
        {
            Opacity = 0;
            Show();
            UpdateLayout();

            var source = PresentationSource.FromVisual(this);
            var toDip = source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformFromDevice : Matrix.Identity;
            var mouse = System.Windows.Forms.Cursor.Position;
            var screen = System.Windows.Forms.Screen.FromPoint(mouse).WorkingArea;
            var at = toDip.Transform(new Point(mouse.X, mouse.Y));
            var min = toDip.Transform(new Point(screen.Left, screen.Top));
            var max = toDip.Transform(new Point(screen.Right, screen.Bottom));

            // normally up and to the left of the pointer (the tray is in a corner); flipped if there is no room
            double left = at.X - ActualWidth + Shadow, top = at.Y - ActualHeight + Shadow;
            if (left < min.X - Shadow) left = at.X - Shadow;
            if (top < min.Y - Shadow) top = at.Y - Shadow;
            Left = Math.Min(left, max.X - ActualWidth + Shadow);
            Top = Math.Min(top, max.Y - ActualHeight + Shadow);

            Activate();
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        }

        void Dismiss()
        {
            if (_closing) return;
            _closing = true;
            Close();
        }
    }
}
