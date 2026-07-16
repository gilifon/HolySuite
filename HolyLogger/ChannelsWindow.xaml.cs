using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Newtonsoft.Json;

namespace HolyLogger
{
    // A user-defined list of radio channels (name / frequency in kHz / mode). Double-clicking a channel
    // asks the main window to set the radio to it (which captures the undo state and pops the undo icon,
    // reusing the same mechanism as "Set Radio to QSO freq"). Tuning is disabled — with a banner — when
    // CAT is not active, but the list stays editable. The list persists as JSON in settings.
    public partial class ChannelsWindow : Window
    {
        public class RadioChannel
        {
            public string Name { get; set; } = "";
            public string FreqKhz { get; set; } = "";
            public string Mode { get; set; } = "";
        }

        private static readonly string[] Modes = { "USB", "LSB", "CW", "FT8", "DIGI", "RTTY", "FM", "AM" };

        private readonly MainWindow _owner;
        private readonly ObservableCollection<RadioChannel> _channels = new ObservableCollection<RadioChannel>();
        private readonly DispatcherTimer _catTimer;

        public ChannelsWindow(MainWindow owner)
        {
            InitializeComponent();
            _owner = owner;
            Owner = owner;
            ModeColumn.ItemsSource = Modes;

            foreach (var ch in LoadChannels())
                _channels.Add(ch);
            ChannelsGrid.ItemsSource = _channels;

            UpdateCatState();
            // CAT can come online / drop while the window is open; keep the banner in sync.
            _catTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
            _catTimer.Tick += (s, e) => UpdateCatState();
            _catTimer.Start();

            Closing += (s, e) => { _catTimer.Stop(); SaveChannels(); };
        }

        private void UpdateCatState()
        {
            bool live = _owner != null && _owner.IsCatLive();
            CatBanner.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        }

        // Restrict the frequency cell's editor to digits and a single decimal point.
        private void FreqBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var tb = sender as System.Windows.Controls.TextBox;
            foreach (char c in e.Text)
            {
                if (char.IsDigit(c)) continue;
                if (c == '.' && tb != null && !tb.Text.Contains(".")) continue;
                e.Handled = true;
                return;
            }
        }

        private void ChannelsGrid_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // Flush any in-progress cell edit so a value just typed (e.g. "7130") is committed to the
            // bound channel before we read it -- otherwise the first double-click would see the old value.
            ChannelsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
            ChannelsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);

            // Ignore the "new item" placeholder row.
            var ch = ChannelsGrid.SelectedItem as RadioChannel;
            if (ch == null)
                return;

            // Reserve double-click for tuning, not for entering cell edit (edit via click / F2 instead).
            e.Handled = true;

            if (_owner == null || !_owner.IsCatLive())
            {
                UpdateCatState();   // the banner explains why nothing happened
                return;
            }

            if (!double.TryParse((ch.FreqKhz ?? string.Empty).Trim(),
                                 NumberStyles.Float, CultureInfo.InvariantCulture, out double khz) || khz <= 0)
            {
                HolyMessageBox.ShowWarning("This channel has no valid frequency (kHz).", "Channels", this);
                return;
            }

            _owner.SetRadioToChannel(khz / 1000.0, ch.Mode);

            // The channel has been applied — close the window so the action feels complete (otherwise,
            // when the channel's frequency is already the current one, nothing visibly happens).
            Close();
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (ChannelsGrid.SelectedItem is RadioChannel ch)
                _channels.Remove(ch);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private static List<RadioChannel> LoadChannels()
        {
            try
            {
                string json = Properties.Settings.Default.ChannelsJson;
                if (string.IsNullOrWhiteSpace(json))
                    return new List<RadioChannel>();
                return JsonConvert.DeserializeObject<List<RadioChannel>>(json) ?? new List<RadioChannel>();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return new List<RadioChannel>(); }
        }

        private void SaveChannels()
        {
            try
            {
                // Drop wholly-empty rows (e.g. an abandoned new-row entry).
                var toSave = _channels
                    .Where(c => !(string.IsNullOrWhiteSpace(c.Name) && string.IsNullOrWhiteSpace(c.FreqKhz)))
                    .ToList();
                Properties.Settings.Default.ChannelsJson = JsonConvert.SerializeObject(toSave);
                Properties.Settings.Default.Save();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }
    }
}
