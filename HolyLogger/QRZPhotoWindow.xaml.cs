
using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Net;

namespace HolyLogger
{
    public partial class QRZPhotoWindow : Window
    {
        private static double? LastLeft;
        private static double? LastTop;
        private static double? LastWidth;
        private static double? LastHeight;

        public QRZPhotoWindow()
        {
            InitializeComponent();
        }

        private static bool IsValidCoord(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        public void SetPhoto(BitmapImage photo)
        {
            PhotoImage.Source = photo;
            StatusText.Visibility = Visibility.Collapsed;
        }

        public void SetPhoto(string imageUrl)
        {
            StatusText.Visibility = Visibility.Collapsed;

            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                StatusText.Text = "QRZ photo not available";
                StatusText.Visibility = Visibility.Visible;
                PhotoImage.Source = null;
                return;
            }

            try
            {
                string normalized = imageUrl.Trim();
                if (normalized.StartsWith("//"))
                {
                    normalized = "https:" + normalized;
                }

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.UriSource = new Uri(normalized, UriKind.Absolute);
                bitmap.EndInit();
                PhotoImage.Source = bitmap;
            }
            catch (Exception ex)
            {
                StatusText.Text = "QRZ photo could not be loaded";
                StatusText.Visibility = Visibility.Visible;
                PhotoImage.Source = null;
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (LastWidth.HasValue && LastHeight.HasValue && IsValidCoord(LastWidth.Value) && IsValidCoord(LastHeight.Value))
            {
                Width = LastWidth.Value;
                Height = LastHeight.Value;
            }

            if (LastLeft.HasValue && LastTop.HasValue && IsValidCoord(LastLeft.Value) && IsValidCoord(LastTop.Value))
            {
                Left = LastLeft.Value;
                Top = LastTop.Value;
            }
            else if (Owner != null)
            {
                Left = Owner.Left + Owner.Width - Width;
                Top = Owner.Top + Owner.Height - Height;
            }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            if (WindowState == WindowState.Normal)
            {
                if (IsValidCoord(Left) && IsValidCoord(Top))
                {
                    LastLeft = Left;
                    LastTop = Top;
                }
            }
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            if (WindowState == WindowState.Normal)
            {
                if (IsValidCoord(Width) && IsValidCoord(Height))
                {
                    LastWidth = Width;
                    LastHeight = Height;
                }
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            if (IsValidCoord(bounds.Left) && IsValidCoord(bounds.Top))
            {
                LastLeft = bounds.Left;
                LastTop = bounds.Top;
            }

            if (IsValidCoord(bounds.Width) && IsValidCoord(bounds.Height))
            {
                LastWidth = bounds.Width;
                LastHeight = bounds.Height;
            }
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        }
    }
}
