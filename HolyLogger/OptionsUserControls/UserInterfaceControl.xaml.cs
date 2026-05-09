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

namespace HolyLogger.OptionsUserControls
{
    /// <summary>
    /// Interaction logic for UserInterfaceControl.xaml
    /// </summary>
    public partial class UserInterfaceControl : UserControl
    {
        public bool HasChanged { get; set; }

        public UserInterfaceControl()
        {
            InitializeComponent();
            HasChanged = false;
            SetCallsignSuggestionRowsSelection();
            SetMapAutoFitMarginSelection();
        }

        private void HasChanged_Click(object sender, RoutedEventArgs e)
        {
            HasChanged = true;
        }

        private void SetCallsignSuggestionRowsSelection()
        {
            int rows = Properties.Settings.Default.CallsignSuggestionRows;
            if (rows != 10 && rows != 15 && rows != 20 && rows != 25 && rows != 30)
            {
                rows = 20;
            }

            foreach (var item in CB_CallsignSuggestionRows.Items)
            {
                ComboBoxItem comboItem = item as ComboBoxItem;
                if (comboItem != null && (string)comboItem.Content == rows.ToString())
                {
                    CB_CallsignSuggestionRows.SelectedItem = comboItem;
                    break;
                }
            }
        }

        private void CB_CallsignSuggestionRows_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ComboBoxItem selected = CB_CallsignSuggestionRows.SelectedItem as ComboBoxItem;
            int rows;
            if (selected == null || !int.TryParse((string)selected.Content, out rows)) return;

            if (Properties.Settings.Default.CallsignSuggestionRows != rows)
            {
                Properties.Settings.Default.CallsignSuggestionRows = rows;
                Properties.Settings.Default.Save();
                HasChanged = true;
            }
        }

        private void SetMapAutoFitMarginSelection()
        {
            try
            {
                // Reload settings from disk to get latest values
                Properties.Settings.Default.Reload();
                
                double margin = Properties.Settings.Default.MapAutoFitMargin;
                // Convert multiplier to percentage: 1.15 -> 15, 1.0 -> 0, etc.
                int percentage = (int)Math.Round((margin - 1.0) * 100);
                
                // Validate percentage is one of the allowed values
                int[] allowedValues = { 0, 5, 10, 15, 20, 25 };
                bool isValid = false;
                foreach (int val in allowedValues)
                {
                    if (percentage == val)
                    {
                        isValid = true;
                        break;
                    }
                }
                
                if (!isValid)
                {
                    percentage = 15; // default
                }

                // Try to find and select the matching item
                bool found = false;
                foreach (var item in CB_MapAutoFitMargin.Items)
                {
                    ComboBoxItem comboItem = item as ComboBoxItem;
                    if (comboItem != null && (string)comboItem.Content == percentage.ToString())
                    {
                        CB_MapAutoFitMargin.SelectedItem = comboItem;
                        found = true;
                        break;
                    }
                }
                
                // If we couldn't find it, select "15" as fallback
                if (!found)
                {
                    foreach (var item in CB_MapAutoFitMargin.Items)
                    {
                        ComboBoxItem comboItem = item as ComboBoxItem;
                        if (comboItem != null && (string)comboItem.Content == "15")
                        {
                            CB_MapAutoFitMargin.SelectedItem = comboItem;
                            break;
                        }
                    }
                }
            }
            catch
            {
                // If setting doesn't exist or is invalid, default to 15%
                foreach (var item in CB_MapAutoFitMargin.Items)
                {
                    ComboBoxItem comboItem = item as ComboBoxItem;
                    if (comboItem != null && (string)comboItem.Content == "15")
                    {
                        CB_MapAutoFitMargin.SelectedItem = comboItem;
                        break;
                    }
                }
            }
        }

        private void CB_MapAutoFitMargin_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ComboBoxItem selected = CB_MapAutoFitMargin.SelectedItem as ComboBoxItem;
            if (selected == null)
                return;
            
            int percentage;
            if (!int.TryParse((string)selected.Content, out percentage))
                return;

            double multiplier = 1.0 + (percentage / 100.0);
            
            // Always save if there's a change, even if difference seems small
            try
            {
                Properties.Settings.Default.MapAutoFitMargin = multiplier;
                Properties.Settings.Default.Save();
                HasChanged = true;
            }
            catch
            {
                // Settings save failed silently
            }
        }

    }
}
