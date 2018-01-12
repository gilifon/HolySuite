using Blue.Windows;
using HolyParser;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace HolyLogger
{
    /// <summary>
    /// Interaction logic for MatrixWindow.xaml
    /// </summary>
    public partial class MatrixWindow : Window
    {
        string x_path = "Images/x.PNG";
        string v_path = "Images/v.PNG";

        BitmapImage x;
        BitmapImage v;

        private StickyWindow _stickyWindow;

        public MatrixWindow()
        {
            InitializeComponent();
            this.Loaded += MatrixWindow_Loaded; ;
            x = new BitmapImage(new Uri(x_path, UriKind.Relative));
            v = new BitmapImage(new Uri(v_path, UriKind.Relative));
            Clear();
        }

        private void MatrixWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _stickyWindow = new StickyWindow(this);
            _stickyWindow.StickToScreen = true;
            _stickyWindow.StickToOther = true;
            _stickyWindow.StickOnResize = true;
            _stickyWindow.StickOnMove = true;
        }

        public void Clear()
        {
            ClearMatrix();
            ClearDup();
        }
        public void ClearMatrix()
        {
            SSB10.Source = x;
            SSB15.Source = x;
            SSB20.Source = x;
            SSB40.Source = x;
            SSB80.Source = x;
            SSB160.Source = x;

            CW10.Source = x;
            CW15.Source = x;
            CW20.Source = x;
            CW40.Source = x;
            CW80.Source = x;
            CW160.Source = x;

            DIGI10.Source = x;
            DIGI15.Source = x;
            DIGI20.Source = x;
            DIGI40.Source = x;
            DIGI80.Source = x;
            DIGI160.Source = x;
        }

        public void SetMatrix(Mode mode, int band)
        {
            switch (mode)
            {
                case Mode.SSB:
                    if (band == 10) SSB10.Source = v;
                    if (band == 15) SSB15.Source = v;
                    if (band == 20) SSB20.Source = v;
                    if (band == 40) SSB40.Source = v;
                    if (band == 80) SSB80.Source = v;
                    if (band == 160) SSB160.Source = v;
                    break;
                case Mode.CW:
                    if (band == 10) CW10.Source = v;
                    if (band == 15) CW15.Source = v;
                    if (band == 20) CW20.Source = v;
                    if (band == 40) CW40.Source = v;
                    if (band == 80) CW80.Source = v;
                    if (band == 160) CW160.Source = v;
                    break;
                case Mode.DIGI:
                    if (band == 10) DIGI10.Source = v;
                    if (band == 15) DIGI15.Source = v;
                    if (band == 20) DIGI20.Source = v;
                    if (band == 40) DIGI40.Source = v;
                    if (band == 80) DIGI80.Source = v;
                    if (band == 160) DIGI160.Source = v;
                    break;
                default:
                    break;
            }
        }

        public void ClearDup()
        {
            L_dup.Visibility = Visibility.Hidden;
        }
        public void SetDup()
        {
            L_dup.Visibility = Visibility.Visible;
        }

        private void MatrixWindow1_LocationChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.MatrixWindowLeft = this.Left;
            Properties.Settings.Default.MatrixWindowTop = this.Top;
        }
    }
}
