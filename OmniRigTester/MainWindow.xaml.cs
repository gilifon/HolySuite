using OmniRig;
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

namespace OmniRigTester
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        #region Constants
        // Constants for enum RigParamX
        private const int PM_UNKNOWN = 0x00000001;
        private const int PM_FREQ = 0x00000002;
        private const int PM_FREQA = 0x00000004;
        private const int PM_FREQB = 0x00000008;
        private const int PM_PITCH = 0x00000010;
        private const int PM_RITOFFSET = 0x00000020;
        private const int PM_RIT0 = 0x00000040;
        private const int PM_VFOAA = 0x00000080;
        private const int PM_VFOAB = 0x00000100;
        private const int PM_VFOBA = 0x00000200;
        private const int PM_VFOBB = 0x00000400;
        private const int PM_VFOA = 0x00000800;
        private const int PM_VFOB = 0x00001000;
        private const int PM_VFOEQUAL = 0x00002000;
        private const int PM_VFOSWAP = 0x00004000;
        private const int PM_SPLITON = 0x00008000;
        private const int PM_SPLITOFF = 0x00010000;
        private const int PM_RITON = 0x00020000;
        private const int PM_RITOFF = 0x00040000;
        private const int PM_XITON = 0x00080000;
        private const int PM_XITOFF = 0x00100000;
        private const int PM_RX = 0x00200000;
        private const int PM_TX = 0x00400000;
        private const int PM_CW_U = 0x00800000;
        private const int PM_CW_L = 0x01000000;
        private const int PM_SSB_U = 0x02000000;
        private const int PM_SSB_L = 0x04000000;
        private const int PM_DIG_U = 0x08000000;
        private const int PM_DIG_L = 0x10000000;
        private const int PM_AM = 0x20000000;
        private const int PM_FM = 0x40000000;

        // Constants for enum RigStatusX
        private const int ST_NOTCONFIGURED = 0x00000000;
        private const int ST_DISABLED = 0x00000001;
        private const int ST_PORTBUSY = 0x00000002;
        private const int ST_NOTRESPONDING = 0x00000003;
        private const int ST_ONLINE = 0x00000004;

        #endregion
        OmniRigX OmniRigEngine;
        RigX Rig;
        bool isSubscribed = false;

        public MainWindow()
        {
            InitializeComponent();
            OmniRigEngine = new OmniRigX();
            Rig = OmniRigEngine.Rig1;
            Subscribe();
        }

        private void Subscribe()
        {
            if (!isSubscribed)
            {
                OmniRigEngine.ParamsChange += OmniRigEngine_ParamsChange;
                OmniRigEngine.StatusChange += OmniRigEngine_StatusChange;
                isSubscribed = true;
            }
        }

        private void OmniRigEngine_StatusChange(int RigNumber)
        {
            if (Rig.Status != OmniRig.RigStatusX.ST_ONLINE)
            {
                this.Dispatcher.Invoke(() =>
                {
                    L_freq.Content = "Offline";
                });
            }
            else
            {

            }
        }

        private void UnSubscribe()
        {
            if (isSubscribed)
            {
                OmniRigEngine.ParamsChange -= OmniRigEngine_ParamsChange;
                isSubscribed = false;
            }
        }

        private void OmniRigEngine_ParamsChange(int RigNumber, int Params)
        {
            this.Dispatcher.Invoke(() =>
            {
                double radioRX = (double)Rig.GetRxFrequency();
                double radioTX = (double)Rig.GetTxFrequency();
                L_freq.Content = radioTX;
                switch (Rig.Mode)
                {
                    case (OmniRig.RigParamX)PM_CW_L:
                        //cmbMode.Text = cmbMode.Items[1].ToString();
                        L_mode.Content = "CW";
                        break;
                    case (OmniRig.RigParamX)PM_CW_U:
                        //cmbMode.Text = cmbMode.Items[0].ToString();
                        L_mode.Content = "CW";
                        break;
                    case (OmniRig.RigParamX)PM_SSB_L:
                        //cmbMode.Text = cmbMode.Items[3].ToString();
                        L_mode.Content = "SSB";
                        break;
                    case (OmniRig.RigParamX)PM_SSB_U:
                        // cmbMode.Text = cmbMode.Items[2].ToString();
                        L_mode.Content = "SSB";
                        break;
                    case (OmniRig.RigParamX)PM_FM:
                        // cmbMode.Text = cmbMode.Items[7].ToString();
                        L_mode.Content = "FM";
                        break;
                    case (OmniRig.RigParamX)PM_AM:
                        // cmbMode.Text = cmbMode.Items[7].ToString();
                        L_mode.Content = "AM";
                        break;
                    case (OmniRig.RigParamX)PM_DIG_L:
                        // cmbMode.Text = cmbMode.Items[7].ToString();
                        L_mode.Content = "DIGI";
                        break;
                    case (OmniRig.RigParamX)PM_DIG_U:
                        // cmbMode.Text = cmbMode.Items[7].ToString();
                        L_mode.Content = "DIGI";
                        break;
                    default:
                        L_mode.Content = "DIGI";
                        break;
                }
            });
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            UnSubscribe();
        }

        private void Button_Click_1(object sender, RoutedEventArgs e)
        {
            Subscribe();
        }
    }
}
