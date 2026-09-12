using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HolyParser;

namespace HolyLogger
{
    // RIGHT-CLICK THE FREQUENCY: come back to this spot later.
    //
    // The cluster already lets a spot be sent to the Try Again list, and that covers the station
    // somebody else has found and named. This covers the one the operator finds himself: he is
    // tuning, he hears somebody worth working, and he cannot read the callsign - a weak signal, a
    // pile-up, or simply somebody in the middle of a transmission. The frequency is all he has and
    // it is enough to find the man again.
    //
    // ONLY "Copy into Try Again". The cluster's menu also offers Copy to Alert, and that one has no
    // meaning here: the alert list rings when a CALLSIGN is spotted, and there is no callsign.
    // Offering it would be offering something that cannot work.
    public partial class MainWindow
    {
        // BOTH FREQUENCY BOXES, because only one of them is ever on screen. The LED readout is what
        // shows when the radio is on CAT; the white box is what shows when it is not. The operator
        // should not have to know which one he is looking at for a right-click to work.
        //
        // PREVIEW, and on the bezel rather than the text box inside it. A TextBox handles the right
        // click itself and puts up its own Cut/Copy/Paste menu, so a handler on the border would
        // never be reached. A preview handler tunnels DOWN from the border first, which is exactly
        // the chance needed - and marking it handled is what stops the editing menu appearing
        // underneath ours.
        private void FreqBox_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            try
            {
                string khz = FrequencyInKhzText();
                if (string.IsNullOrWhiteSpace(khz)) return;   // nothing tuned yet - nothing to keep

                var menu = BuildFrequencyContextMenu(khz);
                menu.PlacementTarget = sender as UIElement;
                menu.IsOpen = true;
                e.Handled = true;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        private ContextMenu BuildFrequencyContextMenu(string khz)
        {
            var menu = new ContextMenu { Style = (Style)FindResource("HolyCtxMenu") };

            string band = string.Empty;
            try { band = (HolyLogParser.convertFreqToBand(TB_Frequency.Text) ?? string.Empty).Trim(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            string mode = ((CB_Mode != null ? CB_Mode.Text : null) ?? string.Empty).Trim().ToUpperInvariant();

            // THE SAME TITLE LINE THE CLUSTER'S MENU USES, minus the parts we do not have. There is
            // no callsign and so no flag; what is left is the frequency in its band's colour and the
            // mode in its own, read exactly as they are read over there.
            var titleContent = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(3, 2, 3, 4)
            };
            titleContent.Children.Add(MenuTitlePart(khz, GetBandBrush(band), 0));
            if (mode.Length > 0)
                titleContent.Children.Add(MenuTitlePart(mode, MenuModeBrush(mode), 6));

            menu.Items.Add(new MenuItem
            {
                Header = titleContent,
                Style = (Style)FindResource("HolyCtxTitle")
            });
            menu.Items.Add(new Separator { Style = (Style)FindResource("HolyCtxSep") });

            var tryAgain = new MenuItem
            {
                Header = "Copy into Try Again",
                Style = (Style)FindResource("HolyCtxItemGo"),
                Icon = MakeRightArrow(),
                ToolTip = "Put this frequency on the Try Again list, to come back to later"
            };
            tryAgain.Click += (s, args) => CopyFrequencyIntoTryAgain(khz, mode, band);
            menu.Items.Add(tryAgain);

            return menu;
        }

        // NO CALLSIGN GOES WITH IT, and that is the whole point - see the note in AddTryAgain about
        // why a blank one is a legal entry and what keeps it safe from the sweep that clears a
        // station once he is worked.
        private void CopyFrequencyIntoTryAgain(string khz, string mode, string band)
        {
            if (dal == null || string.IsNullOrWhiteSpace(khz)) return;

            try
            {
                dal.AddTryAgain(string.Empty, khz, mode, band);
                RefreshTryAgain();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }
    }
}
