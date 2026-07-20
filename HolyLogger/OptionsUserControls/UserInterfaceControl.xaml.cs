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
using WinForms = System.Windows.Forms;

namespace HolyLogger.OptionsUserControls
{
    /// <summary>
    /// Interaction logic for UserInterfaceControl.xaml
    /// </summary>
    public partial class UserInterfaceControl : UserControl
    {
        private bool _isLoadingClusterSettings = false;

        public bool HasChanged { get; set; }

        // Event to notify main window of graphics box mode changes
        public event EventHandler GraphicsBoxModeChanged;

        public UserInterfaceControl()
        {
            InitializeComponent();
            HasChanged = false;
            SetCallsignSuggestionRowsSelection();
            SetMapAutoFitMarginSelection();
            SetMapDistanceUnitSelection();
            LoadMapDisplayModeSettings();
            LoadClusterMapSettings();
            LoadClusterSettings();
        }

        private void HasChanged_Click(object sender, RoutedEventArgs e)
        {
            HasChanged = true;
        }

        private void ShowPhotoFromQRZ_Click(object sender, RoutedEventArgs e)
        {
            HasChanged = true;
            UpdateMapDisplayModeUI();
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

        private void SetMapDistanceUnitSelection()
        {
            string distanceUnit = Properties.Settings.Default.MapDistanceUnit;
            if (!string.Equals(distanceUnit, "Miles", StringComparison.OrdinalIgnoreCase))
            {
                distanceUnit = "KM";
            }

            foreach (var item in CB_MapDistanceUnit.Items)
            {
                ComboBoxItem comboItem = item as ComboBoxItem;
                if (comboItem == null)
                    continue;

                string content = comboItem.Content as string;
                if (string.Equals(content, distanceUnit, StringComparison.OrdinalIgnoreCase))
                {
                    CB_MapDistanceUnit.SelectedItem = comboItem;
                    break;
                }
            }
        }

        private void CB_MapDistanceUnit_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ComboBoxItem selected = CB_MapDistanceUnit.SelectedItem as ComboBoxItem;
            if (selected == null)
                return;

            string value = (selected.Content as string) ?? "KM";
            if (!string.Equals(value, "Miles", StringComparison.OrdinalIgnoreCase))
            {
                value = "KM";
            }

            if (!string.Equals(Properties.Settings.Default.MapDistanceUnit, value, StringComparison.OrdinalIgnoreCase))
            {
                Properties.Settings.Default.MapDistanceUnit = value;
                Properties.Settings.Default.Save();
                HasChanged = true;

                var mainWindow = Application.Current.Windows.OfType<HolyLogger.MainWindow>().FirstOrDefault();
                if (mainWindow != null)
                {
                    mainWindow.RefreshMapAfterUnitChange();
                }
            }
        }

        // (The per-item color pickers that used to live here -- main form background, tables
        // header row, contest exchange -- moved to View > Color Scheme > Customize Colors, where
        // ALL application colors are edited in one place. Legacy values are migrated into the
        // Custom color scheme on first run; see ThemeManager.MigrateLegacyItemColors.)

        private void MapDisplayMode_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;

            int mode = 0; // Default: Map
            bool showGraphicsBox = true;

            if (RB_MapDisplay_None.IsChecked == true)
            {
                mode = -1; // None - show blank panel
                showGraphicsBox = true; // Keep graphics box visible but blank
            }
            else if (RB_MapDisplay_Compass.IsChecked == true)
                mode = 1;
            else if (RB_MapDisplay_QRZPhoto.IsChecked == true)
                mode = 2;
            else if (RB_MapDisplay_CustomImage.IsChecked == true)
                mode = 3;
            else
                mode = 0; // Map

            // Update the display mode setting
            if (Properties.Settings.Default.MapAreaDisplayMode != mode)
            {
                Properties.Settings.Default.MapAreaDisplayMode = mode;
                Properties.Settings.Default.Save();
                HasChanged = true;
            }

            // Update the show/hide setting - always show when using radio buttons
            if (Properties.Settings.Default.IsShowAzimuthControl != showGraphicsBox)
            {
                Properties.Settings.Default.IsShowAzimuthControl = showGraphicsBox;
                Properties.Settings.Default.Save();
                HasChanged = true;
            }

            UpdateMapDisplayModeUI();

            // Trigger immediate graphics box refresh in main window
            GraphicsBoxModeChanged?.Invoke(this, EventArgs.Empty);

            // Auto-open file browser when Custom image is selected for the first time
            if (mode == 3 && string.IsNullOrWhiteSpace(Properties.Settings.Default.CustomMapImagePath))
            {
                Btn_BrowseImage_Click(sender, e);
            }
        }

        private void Btn_BrowseImage_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new WinForms.OpenFileDialog
            {
                Filter = "Image Files (*.jpg, *.jpeg, *.png, *.bmp, *.gif, *.tif, *.tiff)|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|" +
                         "JPEG Files (*.jpg, *.jpeg)|*.jpg;*.jpeg|" +
                         "PNG Files (*.png)|*.png|" +
                         "BMP Files (*.bmp)|*.bmp|" +
                         "GIF Files (*.gif)|*.gif|" +
                         "TIFF Files (*.tif, *.tiff)|*.tif;*.tiff|" +
                         "All Files (*.*)|*.*",
                Title = "Select Custom Image for Graphics Box",
                FilterIndex = 1
            };

            if (dialog.ShowDialog() == WinForms.DialogResult.OK)
            {
                TB_CustomImagePath.Text = dialog.FileName;
                Properties.Settings.Default.CustomMapImagePath = dialog.FileName;
                Properties.Settings.Default.Save();
                HasChanged = true;

                // Trigger immediate graphics box refresh in main window
                GraphicsBoxModeChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void UpdateMapDisplayModeUI()
        {
            // Gray out QRZ Photo option when ShowPhotoFromQRZ is unchecked
            bool showPhotoFromQRZ = CBX_ShowPhotoFromQrz.IsChecked == true;
            RB_MapDisplay_QRZPhoto.IsEnabled = showPhotoFromQRZ;

            // If QRZ Photo option is being disabled and it's currently selected,
            // automatically select Map instead
            if (!showPhotoFromQRZ && RB_MapDisplay_QRZPhoto.IsChecked == true)
            {
                RB_MapDisplay_Map.IsChecked = true;
                Properties.Settings.Default.MapAreaDisplayMode = 0; // Map
                Properties.Settings.Default.Save();
            }

            // Enable/disable custom image path controls based on selection
            bool isCustomSelected = RB_MapDisplay_CustomImage.IsChecked == true;
            TB_CustomImagePath.IsEnabled = isCustomSelected;
            Btn_BrowseImage.IsEnabled = isCustomSelected;
        }

        private void LoadMapDisplayModeSettings()
        {
            // Check if graphics box should be hidden (legacy setting or mode -1)
            bool isShowAzimuth = Properties.Settings.Default.IsShowAzimuthControl;
            int mode = Properties.Settings.Default.MapAreaDisplayMode;

            // If graphics box is hidden via old checkbox setting, select "None"
            if (!isShowAzimuth || mode == -1)
            {
                RB_MapDisplay_None.IsChecked = true;
            }
            else
            {
                // Select the appropriate display mode
                switch (mode)
                {
                    case 1:
                        RB_MapDisplay_Compass.IsChecked = true;
                        break;
                    case 2:
                        RB_MapDisplay_QRZPhoto.IsChecked = true;
                        break;
                    case 3:
                        RB_MapDisplay_CustomImage.IsChecked = true;
                        break;
                    default:
                        RB_MapDisplay_Map.IsChecked = true;
                        break;
                }
            }

            TB_CustomImagePath.Text = Properties.Settings.Default.CustomMapImagePath ?? string.Empty;
            UpdateMapDisplayModeUI();
        }

        private void LoadClusterMapSettings()
        {
            var mainWindow = Application.Current.MainWindow as MainWindow;
            if (mainWindow != null)
            {
                _isLoadingClusterSettings = true;
                try
                {
                    // The hover-popup and "plot spots on map" toggles moved to the Cluster window's gear.
                    CBX_MapShowDayNight.IsChecked = Properties.Settings.Default.MapShowDayNight;
                    bool mapBW = Properties.Settings.Default.MapBlackWhite;
                    RB_MapColor_BW.IsChecked = mapBW;
                    RB_MapColor_Colored.IsChecked = !mapBW;
                }
                finally
                {
                    _isLoadingClusterSettings = false;
                }
            }
        }

        private void MapShowDayNight_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoadingClusterSettings)
                return;

            Properties.Settings.Default.MapShowDayNight = CBX_MapShowDayNight.IsChecked == true;
            try { Properties.Settings.Default.Save(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }

            var mainWindow = Application.Current.MainWindow as MainWindow;
            if (mainWindow != null)
            {
                mainWindow.UpdateMapDayNightOverlay();
            }
        }

        private void MapColorMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoadingClusterSettings)
                return;

            Properties.Settings.Default.MapBlackWhite = RB_MapColor_BW.IsChecked == true;
            try { Properties.Settings.Default.Save(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }

            var mainWindow = Application.Current.MainWindow as MainWindow;
            if (mainWindow != null)
            {
                mainWindow.UpdateMapColorMode();
            }
        }

        private void LoadClusterSettings()
        {
            if (CBX_ClusterActive == null || CBX_ClusterVisible == null)
                return;

            _isLoadingClusterSettings = true;
            try
            {
                CBX_ClusterActive.IsChecked = Properties.Settings.Default.ClusterActive;
                CBX_ClusterVisible.IsChecked = Properties.Settings.Default.ShowClusterWindowOption;
                UpdateClusterVisibleState();
            }
            finally
            {
                _isLoadingClusterSettings = false;
            }
        }

        private void ClusterActive_Changed(object sender, RoutedEventArgs e)
        {
            if (CBX_ClusterActive == null || _isLoadingClusterSettings)
                return;

            bool isActive = CBX_ClusterActive.IsChecked == true;
            Properties.Settings.Default.ClusterActive = isActive;
            try { Properties.Settings.Default.Save(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }

            UpdateClusterVisibleState();

            var optionsWindow = Window.GetWindow(this);

            // Building the cluster window and starting its WebSocket is heavy work. Running it inline
            // here freezes the checkbox and lets the new cluster window steal focus, so the Options
            // window appears to "close". Defer the whole thing to a background dispatcher pass: the
            // checkbox updates instantly, and we re-activate the Options window afterwards so it stays
            // on top until the user closes it themselves.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var mainWindow = Application.Current.MainWindow as MainWindow;
                if (mainWindow != null)
                {
                    mainWindow.HandleClusterActiveChanged(isActive);
                }

                optionsWindow?.Activate();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void ClusterVisible_Changed(object sender, RoutedEventArgs e)
        {
            if (CBX_ClusterVisible == null || _isLoadingClusterSettings)
                return;

            bool isVisible = CBX_ClusterVisible.IsChecked == true;
            Properties.Settings.Default.ShowClusterWindowOption = isVisible;
            try { Properties.Settings.Default.Save(); } catch (System.Exception swallowed) { Log.Swallow(swallowed); }

            var mainWindow = Application.Current.MainWindow as MainWindow;
            if (mainWindow != null)
            {
                mainWindow.HandleClusterVisibilityChanged(isVisible);
            }
        }

        private void UpdateClusterVisibleState()
        {
            if (CBX_ClusterActive == null || CBX_ClusterVisible == null)
                return;

            bool isActive = CBX_ClusterActive.IsChecked == true;

            CBX_ClusterVisible.IsEnabled = isActive;
            CBX_ClusterVisible.Opacity = isActive ? 1.0 : 0.5;
        }

        /// <summary>
        /// Refreshes cluster settings checkboxes from current settings values.
        /// Call this when settings are changed externally (e.g., from View menu).
        /// </summary>
        public void RefreshClusterSettings()
        {
            LoadClusterSettings();
        }

    }
}
