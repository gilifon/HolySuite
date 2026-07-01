using System;
using System.Windows;

namespace HolyLogger
{
    // Reusable dialog to name a log (Create New Log, contest log, Rename). Rejects a name that is
    // already used by another log. For rename, pass excludeId so the log can keep its own name.
    public partial class NewLogWindow : Window
    {
        private readonly DataAccess _dal;
        private readonly long _excludeId;
        public string LogName { get; private set; }

        public NewLogWindow(DataAccess dal, string prompt = "Enter a name for the new log:", string initial = "", long excludeId = 0)
        {
            InitializeComponent();
            _dal = dal;
            _excludeId = excludeId;
            Prompt.Text = prompt;
            TB_Name.Text = initial ?? string.Empty;
            Loaded += (s, e) => { TB_Name.Focus(); TB_Name.SelectAll(); };
        }

        private void Btn_Ok_Click(object sender, RoutedEventArgs e)
        {
            string name = (TB_Name.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(name))
            {
                HolyMessageBox.ShowWarning("Please enter a name.", "Log name", this);
                return;
            }
            if (!_dal.LogNameAvailable(name, _excludeId))
            {
                HolyMessageBox.ShowWarning("A log named \"" + name + "\" already exists. Please choose a different name.", "Name already used", this);
                return;
            }
            LogName = name;
            DialogResult = true;
            Close();
        }
    }
}
