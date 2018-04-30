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
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace HolyLogger
{
    /// <summary>
    /// Interaction logic for MatrixControl.xaml
    /// </summary>
    public partial class MatrixControl : UserControl
    {

        string x_path = "Images/x.PNG";
        string v_path = "Images/v.PNG";

        BitmapImage x;
        BitmapImage v;

        public MatrixControl()
        {
            InitializeComponent();
            x = new BitmapImage(new Uri(x_path, UriKind.Relative));
            v = new BitmapImage(new Uri(v_path, UriKind.Relative));
            Clear();
        }

        public void Clear()
        {
            ClearMatrix();
        }
        public void ClearMatrix()
        {
            SSB10.Source = x;
            SSB12.Source = x;
            SSB15.Source = x;
            SSB17.Source = x;
            SSB20.Source = x;
            SSB30.Source = x;
            SSB40.Source = x;
            SSB80.Source = x;
            SSB160.Source = x;

            CW10.Source = x;
            CW12.Source = x;
            CW15.Source = x;
            CW17.Source = x;
            CW20.Source = x;
            CW30.Source = x;
            CW40.Source = x;
            CW80.Source = x;
            CW160.Source = x;

            DIGI10.Source = x;
            DIGI12.Source = x;
            DIGI15.Source = x;
            DIGI17.Source = x;
            DIGI20.Source = x;
            DIGI30.Source = x;
            DIGI40.Source = x;
            DIGI80.Source = x;
            DIGI160.Source = x;
        }
        public void SetMatrix(Mode mode, string band)
        {
            switch (mode)
            {
                case Mode.SSB:
                    if (band == "10M") SSB10.Source = v;
                    if (band == "12M") SSB12.Source = v;
                    if (band == "15M") SSB15.Source = v;
                    if (band == "17M") SSB17.Source = v;
                    if (band == "20M") SSB20.Source = v;
                    if (band == "30M") SSB30.Source = v;
                    if (band == "40M") SSB40.Source = v;
                    if (band == "80M") SSB80.Source = v;
                    if (band == "160M") SSB160.Source = v;
                    break;
                case Mode.CW:
                    if (band == "10M") CW10.Source = v;
                    if (band == "12M") CW12.Source = v;
                    if (band == "15M") CW15.Source = v;
                    if (band == "17M") CW17.Source = v;
                    if (band == "20M") CW20.Source = v;
                    if (band == "30M") CW30.Source = v;
                    if (band == "40M") CW40.Source = v;
                    if (band == "80M") CW80.Source = v;
                    if (band == "160M") CW160.Source = v;
                    break;
                case Mode.DIGI:
                    if (band == "10M") DIGI10.Source = v;
                    if (band == "12M") DIGI12.Source = v;
                    if (band == "15M") DIGI15.Source = v;
                    if (band == "17M") DIGI17.Source = v;
                    if (band == "20M") DIGI20.Source = v;
                    if (band == "30M") DIGI30.Source = v;
                    if (band == "40M") DIGI40.Source = v;
                    if (band == "80M") DIGI80.Source = v;
                    if (band == "160M") DIGI160.Source = v;
                    break;
                default:
                    break;
            }
        }
    }
}
