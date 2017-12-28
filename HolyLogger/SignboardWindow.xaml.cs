using Blue.Windows;
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
using System.Windows.Shapes;

namespace HolyLogger
{
    /// <summary>
    /// Interaction logic for SignboardWindow.xaml
    /// </summary>
    public partial class SignboardWindow : Window
    {
        public SignboardData signboardData { get; set; }
        private StickyWindow _stickyWindow;

        public SignboardWindow(string call, string square)
        {
            InitializeComponent();
            this.Loaded += SignboardWindow_Loaded; ;

            signboardData = new SignboardData()
            {
                Callsign = call,
                Square = square
            };
            LayoutGrid.DataContext = signboardData;
        }

        private void SignboardWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _stickyWindow = new StickyWindow(this);
            _stickyWindow.StickToScreen = true;
            _stickyWindow.StickToOther = true;
            _stickyWindow.StickOnResize = true;
            _stickyWindow.StickOnMove = true;
        }

        

        public class SignboardData : INotifyPropertyChanged
        {
            private string _callsign;
            public string Callsign
            {
                get
                {
                    return _callsign;
                }
                set
                {
                    _callsign = value;
                    if (PropertyChanged != null)
                    {
                        PropertyChanged(this, new PropertyChangedEventArgs("Callsign"));
                    }
                }
            }

            private string _square;
            public string Square
            {
                get
                {
                    return _square;
                }
                set
                {
                    _square = value;
                    if (PropertyChanged != null)
                    {
                        PropertyChanged(this, new PropertyChangedEventArgs("Square"));
                    }
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }
    }
}
