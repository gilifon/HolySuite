using System.Windows;
using System.Windows.Input;

namespace HolyLogger
{
    // Small "type a name" prompt used by the profiles manager (WPF has no built-in input box).
    public partial class ProfileNameDialog : Window
    {
        public string EnteredName => TB_Name.Text;

        public ProfileNameDialog(string message, string initial)
        {
            InitializeComponent();
            TB_Message.Text = message;
            TB_Name.Text = initial ?? string.Empty;
            Loaded += (s, e) => { TB_Name.Focus(); TB_Name.SelectAll(); };
        }

        private void TB_Name_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { DialogResult = true; Close(); }
        }

        private void Ok_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
        private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
    }
}
