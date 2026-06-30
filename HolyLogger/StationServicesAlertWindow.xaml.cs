using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace HolyLogger
{
    public partial class StationServicesAlertWindow : Window
    {
        // Only the two calls we need — no AttachThreadInput (that caused the deadlock).
        // SetFocus is safe without AttachThreadInput because this window lives on the same
        // UI thread as the caller, so no cross-thread synchronisation is required.
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr hWnd);

        public StationServicesAlertWindow(
            string callsign,
            bool eqslOk, string eqslMsg,
            bool lotwOk, string lotwMsg,
            bool qrzOk,  string qrzMsg)
        {
            InitializeComponent();

            TB_Callsign.Text = callsign;

            bool allOk = eqslOk && lotwOk && qrzOk;
            TB_SubHeader.Text = allOk
                ? "All services are configured for this callsign."
                : "One or more services need attention for this callsign.";
            TB_SubHeader.Foreground = allOk
                ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2E7D32"))
                : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B35C00"));

            AddServiceRow("eQSL",        eqslOk, eqslMsg, isLast: false);
            AddServiceRow("LoTW",        lotwOk, lotwMsg, isLast: false);
            AddServiceRow("QRZ Logbook", qrzOk,  qrzMsg,  isLast: true);

            // Hook Win32 messages so WM_KEYDOWN/Escape is caught at the OS level,
            // bypassing any WPF focus-routing issues entirely.
            SourceInitialized += (s, e) =>
            {
                var src = PresentationSource.FromVisual(this) as HwndSource;
                src?.AddHook(WndHook);
            };

            // Pull Win32 keyboard focus onto this HWND as soon as the window is painted.
            ContentRendered += (s, e) => GrabFocus();

            // Re-assert on every activation (e.g. user alt-tabs back).
            Activated += (s, e) => GrabFocus();
        }

        // Moves Win32 keyboard focus to this dialog's HWND so WM_KEYDOWN is delivered here.
        // SetFocus alone (no AttachThreadInput) is sufficient because this window is on the
        // same thread as the caller.
        private void GrabFocus()
        {
            try
            {
                Activate();
                IntPtr h = new WindowInteropHelper(this).Handle;
                if (h == IntPtr.Zero) return;
                SetForegroundWindow(h);
                SetFocus(h);
                Keyboard.Focus(Btn_Close);
            }
            catch { }
        }

        // WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, VK_ESCAPE = 0x1B.
        // Fires whenever our HWND receives a keydown — catches Escape before WPF sees it.
        private IntPtr WndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if ((msg == 0x0100 || msg == 0x0104) && (int)wParam == 0x1B)
            {
                handled = true;
                Dispatcher.BeginInvoke(new Action(Close));
            }
            return IntPtr.Zero;
        }

        // Belt-and-suspenders WPF layer.
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        }

        private void Btn_Close_Click(object sender, RoutedEventArgs e) => Close();

        private void AddServiceRow(string name, bool ok, string message, bool isLast)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, isLast ? 4 : 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var circle = new Ellipse
            {
                Width = 22, Height = 22,
                Fill = ok
                    ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#34A853"))
                    : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F9A825")),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 0, 0)
            };
            Grid.SetColumn(circle, 0);
            row.Children.Add(circle);

            var icon = new Viewbox
            {
                Width = 14, Height = 14,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(4, 6, 0, 0)
            };
            var path = new Path
            {
                Stretch = Stretch.Uniform,
                Fill = Brushes.White,
                Data = ok
                    ? Geometry.Parse("M9 16.17L4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41z")
                    : Geometry.Parse("M1 21h22L12 2 1 21zm12-3h-2v-2h2v2zm0-4h-2v-4h2v4z")
            };
            icon.Child = path;
            Grid.SetColumn(icon, 0);
            row.Children.Add(icon);

            var textPanel = new StackPanel();
            textPanel.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1565C0"))
            });
            textPanel.Children.Add(new TextBlock
            {
                Text = message,
                FontSize = 12,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#444444")),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            });
            Grid.SetColumn(textPanel, 1);
            row.Children.Add(textPanel);

            ServicesPanel.Children.Add(row);

            if (!isLast)
            {
                ServicesPanel.Children.Add(new Separator
                {
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DDDDDD")),
                    Margin = new Thickness(0, 0, 0, 12),
                    Height = 1
                });
            }
        }
    }
}
