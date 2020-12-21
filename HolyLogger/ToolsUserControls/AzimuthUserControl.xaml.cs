using System;
using System.Collections.Generic;
using System.ComponentModel;
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

namespace HolyLogger.ToolsUserControls
{
    public partial class AzimuthUserControl : UserControl
    {
        public AzimuthData azimuthData { get; set; }
        public AzimuthUserControl()
        {
            InitializeComponent();
            azimuthData = new AzimuthData()
            {
                Azimuth = 0
            };
            azimuthData.PropertyChanged += AzimuthData_PropertyChanged;
        }

        private void AzimuthData_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            RotateTransform rotateTransform = new RotateTransform(azimuthData.Azimuth, Needle.Width/2,Needle.Height/2);
            Needle.RenderTransform = rotateTransform;
            Lbl_Azimuth.Content = Math.Round(azimuthData.Azimuth,0);
        }

        public class AzimuthData : INotifyPropertyChanged
        {
            private double _azimuth;
            public double Azimuth
            {
                get
                {
                    return _azimuth;
                }
                set
                {
                    _azimuth = value;

                    if (PropertyChanged != null)
                    {
                        PropertyChanged(this, new PropertyChangedEventArgs("Azimuth"));
                    }
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }
    }
}
