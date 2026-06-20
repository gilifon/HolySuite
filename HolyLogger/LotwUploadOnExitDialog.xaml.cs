using System.Windows;

namespace HolyLogger
{
    public enum LotwExitChoice { Upload, ExitWithout, Cancel }

    public partial class LotwUploadOnExitDialog : Window
    {
        public LotwExitChoice Choice { get; private set; } = LotwExitChoice.Cancel;

        public LotwUploadOnExitDialog(int qsoCount)
        {
            InitializeComponent();
            CountRun.Text = $"{qsoCount:N0}";
        }

        private void YesBtn_Click(object sender, RoutedEventArgs e)
        {
            Choice = LotwExitChoice.Upload;
            Close();
        }

        private void NoBtn_Click(object sender, RoutedEventArgs e)
        {
            Choice = LotwExitChoice.ExitWithout;
            Close();
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            Choice = LotwExitChoice.Cancel;
            Close();
        }
    }
}
