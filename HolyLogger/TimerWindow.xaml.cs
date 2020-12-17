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
    /// Interaction logic for TimerWindow.xaml
    /// </summary>
    public partial class TimerWindow : Window
    {
        private StickyWindow _stickyWindow;
        CountDownTimer timer;

        public TimerWindow(string call)
        {
            InitializeComponent();
            this.Loaded += TimerWindow_Loaded;
            this.Closed += TimerWindow_Closed;
            LayoutGrid.DataContext = this;


            timer = new CountDownTimer();
            timer.SetTime(60);
            timerlbl.Content = timer.TimeLeftStr;
            //update label text
            timer.TimeChanged += TimeChanged;
            timer.CountDownFinished += () => MessageBox.Show("Timer finished the work!");
            timer.StepMs = 77; // for nice milliseconds time switch
        }

        private void TimeChanged()
        {
            this.Dispatcher.Invoke(() =>
            {
                timerlbl.Content = timer.TimeLeftStr;
            });
        }

        private void TimerWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _stickyWindow = new StickyWindow(this);
            _stickyWindow.StickToScreen = true;
            _stickyWindow.StickToOther = true;
            _stickyWindow.StickOnResize = true;
            _stickyWindow.StickOnMove = true;
        }

        private void TimerWindow_Closed(object sender, EventArgs e)
        {
            timer.Stop();
            timer.Dispose();
        }


        private void TimerWindow1_LocationChanged(object sender, EventArgs e)
        {
            if (this.Left >= 0)
            Properties.Settings.Default.TimerWindowLeft = this.Left;
            if (this.Top >= 0)
                Properties.Settings.Default.TimerWindowTop = this.Top;
        }

        private void TimerWindow1_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (this.Width >= 0)
                Properties.Settings.Default.TimerWindowWidth = this.Width;
            if (this.Height >= 0)
                Properties.Settings.Default.TimerWindowHeight = this.Height;
        }

        private void ResetBtn_Click(object sender, RoutedEventArgs e)
        {
            timer.Stop();
            timer.Reset();
            StartStopBtn.Content = "Start";
            timerlbl.Content = timer.TimeLeftStr;
        }

        private void StartStopBtn_Click(object sender, RoutedEventArgs e)
        {
            if (timer!= null)
            {
                if (timer.IsRunnign)
                {
                    timer.Pause();
                    StartStopBtn.Content = "Start";
                }
                else
                {
                    timer.Start();
                    StartStopBtn.Content = "Stop";
                }
            }
        }
    }
}
