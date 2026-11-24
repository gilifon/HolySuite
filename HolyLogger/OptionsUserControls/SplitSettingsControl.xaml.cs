using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace HolyLogger.OptionsUserControls
{
    /// <summary>
    /// The Split button's settings: range, wheel step and the distance that ends split, for SSB and
    /// the other modes and for CW. Shown on the Radio Control Panel page of Options and, on its own,
    /// in the window opened by right-clicking the Split button.
    /// </summary>
    public partial class SplitSettingsControl : UserControl
    {
        public SplitSettingsControl()
        {
            InitializeComponent();
            TB_SplitFrom.Text = Properties.Settings.Default.SplitRangeFromKhz.ToString(CultureInfo.InvariantCulture);
            TB_SplitTo.Text = Properties.Settings.Default.SplitRangeToKhz.ToString(CultureInfo.InvariantCulture);
            double step = Properties.Settings.Default.SplitStepKhz;
            if (Math.Abs(step - 0.1) < 0.001) RB_Step01.IsChecked = true;
            else if (Math.Abs(step - 0.5) < 0.001) RB_Step05.IsChecked = true;
            else RB_Step1.IsChecked = true;
            LoadCwSplitSettings();
            TB_SplitLeave.Text = Properties.Settings.Default.SplitLeaveKhz.ToString(CultureInfo.InvariantCulture);
            TB_SplitCwLeave.Text = Properties.Settings.Default.SplitCwLeaveKhz.ToString(CultureInfo.InvariantCulture);
        }

        private void SplitBox_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            e.Handled = !e.Text.All(char.IsDigit);
        }

        private void SplitBox_LostFocus(object sender, RoutedEventArgs e)
        {
            var s = Properties.Settings.Default;
            int from, to;
            if (!int.TryParse(TB_SplitFrom.Text, NumberStyles.None, CultureInfo.InvariantCulture, out from) || from < 1 || from > 99) from = s.SplitRangeFromKhz;
            if (!int.TryParse(TB_SplitTo.Text, NumberStyles.None, CultureInfo.InvariantCulture, out to) || to < 1 || to > 99) to = s.SplitRangeToKhz;
            if (from > to)
            {
                from = s.SplitRangeFromKhz;
                to = s.SplitRangeToKhz;
            }
            TB_SplitFrom.Text = from.ToString(CultureInfo.InvariantCulture);
            TB_SplitTo.Text = to.ToString(CultureInfo.InvariantCulture);
            if (from != s.SplitRangeFromKhz || to != s.SplitRangeToKhz)
            {
                s.SplitRangeFromKhz = from;
                s.SplitRangeToKhz = to;
                s.Save();
            }
        }

        private void SplitCwBox_LostFocus(object sender, RoutedEventArgs e)
        {
            var s = Properties.Settings.Default;
            int from, to;
            if (!int.TryParse(TB_SplitCwFrom.Text, NumberStyles.None, CultureInfo.InvariantCulture, out from) || from < 1 || from > 99) from = s.SplitCwRangeFromKhz;
            if (!int.TryParse(TB_SplitCwTo.Text, NumberStyles.None, CultureInfo.InvariantCulture, out to) || to < 1 || to > 99) to = s.SplitCwRangeToKhz;
            if (from > to)
            {
                from = s.SplitCwRangeFromKhz;
                to = s.SplitCwRangeToKhz;
            }
            TB_SplitCwFrom.Text = from.ToString(CultureInfo.InvariantCulture);
            TB_SplitCwTo.Text = to.ToString(CultureInfo.InvariantCulture);
            if (from != s.SplitCwRangeFromKhz || to != s.SplitCwRangeToKhz)
            {
                s.SplitCwRangeFromKhz = from;
                s.SplitCwRangeToKhz = to;
                s.Save();
            }
        }

        private void SplitLeave_LostFocus(object sender, RoutedEventArgs e)
        {
            var s = Properties.Settings.Default;
            int v;
            if (!int.TryParse(TB_SplitLeave.Text, NumberStyles.None, CultureInfo.InvariantCulture, out v) || v < 1 || v > 99) v = s.SplitLeaveKhz;
            TB_SplitLeave.Text = v.ToString(CultureInfo.InvariantCulture);
            if (v != s.SplitLeaveKhz)
            {
                s.SplitLeaveKhz = v;
                s.Save();
            }
        }

        private void SplitCwLeave_LostFocus(object sender, RoutedEventArgs e)
        {
            var s = Properties.Settings.Default;
            int v;
            if (!int.TryParse(TB_SplitCwLeave.Text, NumberStyles.None, CultureInfo.InvariantCulture, out v) || v < 1 || v > 99) v = s.SplitCwLeaveKhz;
            TB_SplitCwLeave.Text = v.ToString(CultureInfo.InvariantCulture);
            if (v != s.SplitCwLeaveKhz)
            {
                s.SplitCwLeaveKhz = v;
                s.Save();
            }
        }

        private void SplitStep_Checked(object sender, RoutedEventArgs e)
        {
            double step;
            var rb = sender as RadioButton;
            if (rb == null || !double.TryParse((string)rb.Tag, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out step)) return;
            if (Math.Abs(Properties.Settings.Default.SplitStepKhz - step) < 0.0001) return;
            Properties.Settings.Default.SplitStepKhz = step;
            Properties.Settings.Default.Save();
        }

        private void SplitCwStep_Checked(object sender, RoutedEventArgs e)
        {
            double step;
            var rb = sender as RadioButton;
            if (rb == null || !double.TryParse((string)rb.Tag, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out step)) return;
            if (Math.Abs(Properties.Settings.Default.SplitCwStepKhz - step) < 0.0001) return;
            Properties.Settings.Default.SplitCwStepKhz = step;
            Properties.Settings.Default.Save();
        }

        private void LoadCwSplitSettings()
        {
            var s = Properties.Settings.Default;
            TB_SplitCwFrom.Text = s.SplitCwRangeFromKhz.ToString(CultureInfo.InvariantCulture);
            TB_SplitCwTo.Text = s.SplitCwRangeToKhz.ToString(CultureInfo.InvariantCulture);
            double step = s.SplitCwStepKhz;
            if (Math.Abs(step - 0.1) < 0.001) RB_CwStep01.IsChecked = true;
            else if (Math.Abs(step - 0.5) < 0.001) RB_CwStep05.IsChecked = true;
            else RB_CwStep1.IsChecked = true;
        }
    }
}
