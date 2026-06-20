using System.Windows;

namespace HolyLogger
{
    public partial class ServiceUploadOnExitDialog : Window
    {
        public enum Result { Cancel, Yes, No }
        public Result DialogResult2 { get; private set; } = Result.Cancel;

        public ServiceUploadOnExitDialog(string serviceName, int pendingCount)
        {
            InitializeComponent();
            CountRun.Text = $"{pendingCount} QSO{(pendingCount != 1 ? "s" : "")} pending for ";
            ServiceRun.Text = serviceName;
            ServiceRun.FontWeight = FontWeights.Bold;
        }

        private void YesBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult2 = Result.Yes;
            Close();
        }

        private void NoBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult2 = Result.No;
            Close();
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult2 = Result.Cancel;
            Close();
        }
    }
}
