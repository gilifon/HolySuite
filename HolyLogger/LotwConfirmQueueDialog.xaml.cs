using System.Windows;

namespace HolyLogger
{
    public partial class LotwConfirmQueueDialog : Window
    {
        public bool Confirmed { get; private set; }

        public LotwConfirmQueueDialog(string displayDate, int qsoCount)
        {
            InitializeComponent();
            DateRun.Text = displayDate;
            CountRun.Text = $"{qsoCount:N0}";
        }

        private void YesBtn_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = true;
            Close();
        }

        private void NoBtn_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = false;
            Close();
        }
    }
}
