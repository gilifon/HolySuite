using HolyParser;
using DXCCManager;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HolyLogger
{
    // Date / Time entry rules for the Search window's editable cells.
    //
    // These REFUSE a badly formed value instead of quietly discarding it. The converters alone were
    // not enough: their ConvertBack returns Binding.DoNothing for junk, which leaves the stored value
    // safe but tells the operator nothing - the cell simply appears to accept "06:2" and moves on. A
    // failing rule keeps the cell in edit with a red border, so the value has to be corrected (or Esc
    // pressed to abandon the change) before the cell can be left.
    public class QsoDateRule : ValidationRule
    {
        internal static readonly string[] Accepted =
            { "dd-MM-yyyy", "dd/MM/yyyy", "dd.MM.yyyy", "yyyyMMdd", "yyyy-MM-dd" };

        public override ValidationResult Validate(object value, System.Globalization.CultureInfo culture)
        {
            string typed = (value as string)?.Trim();
            if (string.IsNullOrWhiteSpace(typed))
                return new ValidationResult(false, "A QSO must have a date — e.g. 19-04-2025.");

            return DateTime.TryParseExact(typed, Accepted,
                       System.Globalization.CultureInfo.InvariantCulture,
                       System.Globalization.DateTimeStyles.None, out DateTime _)
                ? ValidationResult.ValidResult
                : new ValidationResult(false, "Date must look like 19-04-2025.");
        }
    }

    public class QsoTimeRule : ValidationRule
    {
        internal static readonly string[] Accepted = { "HH:mm:ss", "HH:mm", "HHmmss", "HHmm" };

        public override ValidationResult Validate(object value, System.Globalization.CultureInfo culture)
        {
            string typed = (value as string)?.Trim();
            if (string.IsNullOrWhiteSpace(typed))
                return new ValidationResult(false, "A QSO must have a time — e.g. 06:22:00.");

            return DateTime.TryParseExact(typed, Accepted,
                       System.Globalization.CultureInfo.InvariantCulture,
                       System.Globalization.DateTimeStyles.None, out DateTime _)
                ? ValidationResult.ValidResult
                : new ValidationResult(false, "Time must look like 06:22 or 06:22:00.");
        }
    }

    public partial class SearchWindow : Window
    {
        private ObservableCollection<QSO> _allQsos;
        private System.Collections.Generic.List<SearchCountryItem> _allCountries;
        private ListCollectionView _countriesView;
        private string _countryFilter = "";
        private TextBox _countryEditBox;   // the ComboBox's internal editable text box

        // Clear button: blue when there is something to clear, gray (but still enabled) otherwise.
        private static readonly Brush ClearActiveBrush = new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0));
        private static readonly Brush ClearIdleBrush   = new SolidColorBrush(Color.FromRgb(0x75, 0x75, 0x75));

        // The Search button's own blue and its darker edge, the pair it is given in the XAML. Held here
        // because the button now changes colour with the filters and something has to put them back.
        private static readonly Brush SearchActiveBrush = new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0));
        private static readonly Brush SearchBorderBrush = new SolidColorBrush(Color.FromRgb(0x0D, 0x47, 0xA1));

        // A click on a callsign in the results opens that station's QRZ.com page in the default
        // browser — the callsign acts like a web link (hand cursor + "QRZ" tooltip in the XAML).
        // Gated on ClickCount==1 so a double-click opens the page once, not twice.
        private void OpenQrz(string callsign)
        {
            string call = (callsign ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(call)) return;
            try { System.Diagnostics.Process.Start("https://www.qrz.com/db/" + call); }
            catch (Exception ex) { Log.Swallow(ex); }
        }

        // ===== Right-click row menu: Search QRZ / Edit / Delete / send-to-upload-queue =====

        private DataGridRow _hlRow;          // the row highlighted while its menu / editor is up
        private Brush _hlOrigBg;             // its own background, to restore afterwards (null if it had none)
        private bool _hlHadLocalBg;          // did it have a background of its own at all? see HighlightRow
        private bool _hlKeep;                // keep the highlight past the menu close (an editor is open)

        private void HighlightRow(DataGridRow row)
        {
            ClearHighlight();
            if (row == null) return;
            _hlRow = row;
            // Note whether the row had a background of its OWN, as opposed to one it merely picks up from
            // the grid's alternating colours or from the ticked-row trigger. Putting an inherited colour
            // back as a local one would freeze the row on it: a local value outranks a style trigger, so
            // unticking that row later would leave it looking selected for good.
            _hlHadLocalBg = row.ReadLocalValue(BackgroundProperty) != DependencyProperty.UnsetValue;
            _hlOrigBg = _hlHadLocalBg ? row.Background : null;
            row.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x82));   // clear amber
        }

        private void ClearHighlight()
        {
            if (_hlRow == null) return;
            if (_hlHadLocalBg) _hlRow.Background = _hlOrigBg;
            else _hlRow.ClearValue(BackgroundProperty);   // back to the trigger / alternating colour
            _hlRow = null; _hlOrigBg = null; _hlHadLocalBg = false;
        }

        private static T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T t) return t;
                child = VisualTreeHelper.GetParent(child);
            }
            return null;
        }

        // ONE THING, NOT TWO. Highlighting a row and ticking its box are now the same act: clicking a row
        // highlights it and ticks it, Ctrl+clicking another adds that one too, and the tick boxes go on
        // working exactly as they did. The selection is the master and the ticks follow it.
        //
        // The PAPER QSL box is the exception and always will be: that tick is a fact about the contact -
        // a card in the drawer - not a way of choosing rows. A click on it must leave the selection
        // alone, or ticking a card would silently pick that row and drop every other.
        private void ResultsGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var box = FindVisualParent<CheckBox>(e.OriginalSource as DependencyObject);
            if (box == null) return;
            if ((box.Tag as string) == "PickBox") return;    // the pick box: selecting is its whole job

            // Everything else with a tick in it - Paper QSL today - keeps its hands off the selection.
            // Not enough to stop the ticks following: WPF still SELECTS the row the box sits in, so the
            // row would light up while its pick box stayed empty and the two would openly disagree. The
            // selection is put back the way it was, from the ticks, once the click has been delivered.
            _selectionFrozen = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    _syncingPicks = true;
                    ResultsGrid.UnselectAll();
                    foreach (QSO q in ResultsGrid.Items.OfType<QSO>())
                        if (q.IsPicked) ResultsGrid.SelectedItems.Add(q);
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
                finally { _syncingPicks = false; _selectionFrozen = false; }
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        // True while a click on a data checkbox is being delivered, so the selection that WPF makes
        // underneath it is not turned into ticks.
        private bool _selectionFrozen;
        private bool _syncingPicks;

        private void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_selectionFrozen || _syncingPicks) return;

            try
            {
                _syncingPicks = true;
                foreach (object item in e.RemovedItems)
                {
                    var q = item as QSO;
                    if (q != null) q.IsPicked = false;
                }
                foreach (object item in e.AddedItems)
                {
                    var q = item as QSO;
                    if (q != null) q.IsPicked = true;
                }
            }
            catch (Exception ex) { Log.Swallow(ex); }
            finally { _syncingPicks = false; }

            UpdatePickState();
        }

        // EDITING IS A DOUBLE-CLICK, and nothing else. The grid is held read-only so a single click can
        // do the one thing a single click should do - choose the row - without dropping a cell into edit
        // mode under the operator's hand. A double-click opens that cell, and the grid is closed again as
        // soon as the edit ends. Columns that are read-only in their own right stay read-only: the flag
        // below only lifts the grid-wide lock, it cannot make a computed column editable.
        private void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            try
            {
                var cell = FindVisualParent<DataGridCell>(e.OriginalSource as DependencyObject);
                if (cell == null || cell.Column == null || cell.Column.IsReadOnly) return;
                if (FindVisualParent<CheckBox>(e.OriginalSource as DependencyObject) != null) return;

                ResultsGrid.IsReadOnly = false;
                ResultsGrid.CurrentCell = new DataGridCellInfo(cell);
                ResultsGrid.BeginEdit();
            }
            catch (Exception ex) { Log.Swallow(ex); }
        }

        // Whatever ended the edit - Enter, Esc, clicking away - the lock goes back on.
        private void RelockAfterEdit()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { ResultsGrid.IsReadOnly = true; }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void ResultsGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var row = FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
            if (row == null) { e.Handled = true; return; }     // header / empty area: no menu
            var qso = row.Item as QSO;
            if (qso == null) { e.Handled = true; return; }

            // A right-click that lands OUTSIDE the selection drops it. The menu always acts on what the
            // mouse is pointing at, so ticks left standing on rows elsewhere would be a trap - the
            // operator would be looking at one row while the menu spoke for a dozen others.
            if (!qso.IsPicked)
            {
                ClearPicks();
                UpdatePickState();
            }

            // With several rows ticked and the click landing on one of them, the menu speaks for the whole
            // selection - which the blue highlight already shows, so the single-row amber would only
            // muddle the picture and is left off.
            var picked = ResultsGrid.Items.OfType<QSO>().Where(q => q.IsPicked).ToList();
            bool many = picked.Count > 1 && qso.IsPicked;

            // Colour the row so it is unmistakable which QSO the menu (and the editor) act on.
            if (!many) HighlightRow(row);

            var menu = many ? BuildSelectionContextMenu(picked) : BuildRowContextMenu(qso, row);

            // Copy the cell under the mouse, or the selected rows. Added here because only this
            // handler knows which cell was right-clicked - see GridCopy.
            AddCopyItems(menu, ResultsGrid, e.OriginalSource);

            menu.Closed += (s, _) => { if (!_hlKeep) ClearHighlight(); _hlKeep = false; };
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.PlacementTarget = ResultsGrid;
            menu.IsOpen = true;
            e.Handled = true;   // suppress any default context menu
        }

        // A separator and the two copy items, in the menu's own style. The twin of the main window's
        // AddCopyItems; both tables get the same two commands so the habit carries between them.
        private static void AddCopyItems(ContextMenu menu, DataGrid grid, object rightClickedOn)
        {
            if (menu == null || grid == null) return;
            try
            {
                var cell = GridCopy.CellFrom(rightClickedOn);
                string cellText = GridCopy.TextOf(cell);

                // The FIRST REAL MenuItem, not Items[0]: this menu opens with a title block naming the
                // QSO, so index 0 is not a MenuItem and the style came back null - which left the two
                // copy lines in the default font. The separator is matched the same way.
                Style itemStyle = null, sepStyle = null;
                foreach (object o in menu.Items)
                {
                    if (itemStyle == null && o is MenuItem) itemStyle = ((MenuItem)o).Style;
                    if (sepStyle == null && o is Separator) sepStyle = ((Separator)o).Style;
                    if (itemStyle != null && sepStyle != null) break;
                }

                menu.Items.Add(sepStyle == null ? new Separator() : new Separator { Style = sepStyle });
                menu.Items.Add(GridCopy.CopyCellItem(cellText, itemStyle));
                menu.Items.Add(GridCopy.CopyRowsItem(grid, itemStyle));
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // The same styled menu resources the main window's log right-click menu uses (rounded white card,
        // blue hover, red Delete). Parsed once, lazily.
        private ResourceDictionary _ctxRes;
        private ResourceDictionary CtxRes
        {
            get
            {
                if (_ctxRes == null)
                {
                    const string xaml =
@"<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                     xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style x:Key='CtxMenu' TargetType='ContextMenu'>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ContextMenu'>
          <Border Background='#FFFFFF' BorderBrush='#1565C0' BorderThickness='1.5' CornerRadius='10' Padding='6' SnapsToDevicePixels='True'>
            <Border.Effect>
              <DropShadowEffect BlurRadius='14' ShadowDepth='2' Opacity='0.35' Color='#666666'/>
            </Border.Effect>
            <StackPanel IsItemsHost='True' KeyboardNavigation.DirectionalNavigation='Cycle'/>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <ControlTemplate x:Key='CtxItemTemplate' TargetType='MenuItem'>
    <Border x:Name='bd' Background='Transparent' CornerRadius='6' Padding='{TemplateBinding Padding}'>
      <Grid>
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width='24'/>
          <ColumnDefinition Width='*'/>
        </Grid.ColumnDefinitions>
        <ContentPresenter Grid.Column='0' ContentSource='Icon' VerticalAlignment='Center' HorizontalAlignment='Center'/>
        <ContentPresenter Grid.Column='1' ContentSource='Header' VerticalAlignment='Center' Margin='8,0,0,0'/>
      </Grid>
    </Border>
    <ControlTemplate.Triggers>
      <Trigger Property='IsHighlighted' Value='True'>
        <Setter TargetName='bd' Property='Background' Value='#1565C0'/>
        <Setter Property='Foreground' Value='White'/>
      </Trigger>
      <Trigger Property='IsEnabled' Value='False'>
        <Setter Property='Foreground' Value='#AAAAAA'/>
      </Trigger>
    </ControlTemplate.Triggers>
  </ControlTemplate>
  <ControlTemplate x:Key='CtxItemDangerTemplate' TargetType='MenuItem'>
    <Border x:Name='bd' Background='Transparent' CornerRadius='6' Padding='{TemplateBinding Padding}'>
      <Grid>
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width='24'/>
          <ColumnDefinition Width='*'/>
        </Grid.ColumnDefinitions>
        <ContentPresenter Grid.Column='0' ContentSource='Icon' VerticalAlignment='Center' HorizontalAlignment='Center'/>
        <ContentPresenter Grid.Column='1' ContentSource='Header' VerticalAlignment='Center' Margin='8,0,0,0'/>
      </Grid>
    </Border>
    <ControlTemplate.Triggers>
      <Trigger Property='IsHighlighted' Value='True'>
        <Setter TargetName='bd' Property='Background' Value='#D32F2F'/>
        <Setter Property='Foreground' Value='White'/>
      </Trigger>
    </ControlTemplate.Triggers>
  </ControlTemplate>
  <Style x:Key='CtxItem' TargetType='MenuItem'>
    <Setter Property='FontSize' Value='15'/>
    <Setter Property='Foreground' Value='#1A1A1A'/>
    <Setter Property='Padding' Value='12,7'/>
    <Setter Property='Margin' Value='2,1'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template' Value='{StaticResource CtxItemTemplate}'/>
  </Style>
  <Style x:Key='CtxItemDanger' TargetType='MenuItem' BasedOn='{StaticResource CtxItem}'>
    <Setter Property='Foreground' Value='#C62828'/>
    <Setter Property='Template' Value='{StaticResource CtxItemDangerTemplate}'/>
  </Style>
  <Style x:Key='CtxSep' TargetType='Separator'>
    <Setter Property='Margin' Value='8,5'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Separator'>
          <Border Height='1' Background='#BDBDBD' SnapsToDevicePixels='True'/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>";
                    _ctxRes = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml);
                }
                return _ctxRes;
            }
        }

        private static TextBlock MakeMenuGlyph(string glyph, Brush color)
        {
            return new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 16,
                Foreground = color,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
        }

        // ONE AI WINDOW AT A TIME, and it belongs to this one. A second report for another row, side
        // by side with the first, is two windows the operator has to tell apart by reading them.
        private AiQsoCheckWindow _aiCheckWindow;

        private void OpenAiQsoCheck(QSO qso)
        {
            if (qso == null) return;
            try
            {
                if (_aiCheckWindow != null)
                {
                    _aiCheckWindow.Close();
                    _aiCheckWindow = null;
                }

                var window = new AiQsoCheckWindow(qso, this);
                window.Closed += (s, e) => { if (ReferenceEquals(_aiCheckWindow, window)) _aiCheckWindow = null; };
                _aiCheckWindow = window;
                window.Show();
            }
            catch (Exception ex)
            {
                HolyMessageBox.ShowError("Could not open the AI check: " + ex.Message, "AI check", this);
            }
        }

        private ContextMenu BuildRowContextMenu(QSO qso, DataGridRow row)
        {
            var res = CtxRes;
            var itemStyle = (Style)res["CtxItem"];
            var dangerStyle = (Style)res["CtxItemDanger"];
            var sepStyle = (Style)res["CtxSep"];
            var blue = (Brush)new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0));
            var red = (Brush)new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));

            var menu = new ContextMenu { Style = (Style)res["CtxMenu"] };

            // Whose QSO this menu is about, spelled out at the top. Delete and the upload queue act
            // without a second look at the table, and the row underneath is half-covered by the menu
            // itself - so the callsign says it here, with date / band / mode beneath to pin down WHICH
            // contact with that station it is.
            menu.Items.Add(RowMenuParts.MakeMenuTitle(qso.DXCall, RowMenuParts.QsoSubtitle(qso)));
            menu.Items.Add(new Separator { Style = sepStyle });

            var qrz = new MenuItem { Header = "Search QRZ", Style = itemStyle, Icon = MakeMenuGlyph("", blue) };
            qrz.Click += (s, e) => OpenQrz(qso.DXCall);
            menu.Items.Add(qrz);

            // ONE QSO, READ BY AN AI. Reports only; it never writes to the log. The same item, with the
            // same caption, sits in the main window's row menu and in Verify's.
            var ai = new MenuItem { Header = RowMenuParts.MakeAiHeader(), Style = itemStyle, Icon = MakeMenuGlyph("", blue) };
            ai.Click += (s, e) =>
            {
                _hlKeep = true;
                Dispatcher.BeginInvoke(new Action(() => OpenAiQsoCheck(qso)),
                                       System.Windows.Threading.DispatcherPriority.Background);
            };
            menu.Items.Add(ai);

            // Edit / Delete are deferred until the menu has fully closed (the editor is modal; running it
            // while the menu is still dismissing and holding mouse capture leaves it unable to take the
            // click). _hlKeep keeps the row highlighted across the close.
            var edit = new MenuItem { Header = "Full View & Edit", Style = itemStyle, Icon = MakeMenuGlyph("", blue) };
            edit.Click += (s, e) =>
            {
                _hlKeep = true;
                Dispatcher.BeginInvoke(new Action(() => EditQso(qso, row)), System.Windows.Threading.DispatcherPriority.Background);
            };
            menu.Items.Add(edit);

            var del = new MenuItem { Header = "Delete", Style = dangerStyle, Icon = MakeMenuGlyph("", red) };
            del.Click += (s, e) =>
            {
                _hlKeep = true;
                Dispatcher.BeginInvoke(new Action(() => DeleteQso(qso)), System.Windows.Threading.DispatcherPriority.Background);
            };
            menu.Items.Add(del);

            menu.Items.Add(new Separator { Style = sepStyle });

            var header = new MenuItem { Header = "Send to upload queue for:", Style = itemStyle, IsEnabled = false, Icon = MakeMenuGlyph("", blue) };
            menu.Items.Add(header);

            // Real CheckBox controls (not checkable menu items) so each logger shows a visible box. Hosting
            // them directly in the menu keeps it open while you tick several, until OK.
            var s0 = Properties.Settings.Default;
            var cbLotw = RowMenuParts.MakeServiceCheck("LoTW", s0.UseLotwService);
            var cbQrz  = RowMenuParts.MakeServiceCheck("QRZ", s0.UseQrzLogbook);
            var cbEqsl = RowMenuParts.MakeServiceCheck("eQSL", s0.UseEqslService);
            var cbClub = RowMenuParts.MakeServiceCheck("Club Log", s0.UseClublogService);
            menu.Items.Add(RowMenuParts.MakeServiceGrid(cbLotw, cbQrz, cbEqsl, cbClub));

            var ok = new Button
            {
                Content = "OK",
                Width = 80,
                FontSize = 16,
                Padding = new Thickness(10, 4, 10, 4),
                Cursor = Cursors.Hand
            };
            ok.Click += (s, e) =>
            {
                QueueForUpload(qso, cbLotw.IsChecked == true, cbQrz.IsChecked == true,
                               cbEqsl.IsChecked == true, cbClub.IsChecked == true);
                menu.IsOpen = false;
            };
            menu.Items.Add(MakeButtonRow(ok, RowMenuParts.MakeCloseButton(menu)));

            return menu;
        }

        // OK and Close side by side at the foot of the card. OK belongs to the logger boxes above it;
        // Close ends the menu whether or not anything was ticked.
        //
        // CENTRED UNDER THE CARD, not indented to the menu's text margin. Every other line here begins
        // at 40px because it is a line of text with a glyph to its left; these two are not a line of
        // text, they are the pair of buttons the card ends with, and starting them at the text indent
        // left them hanging off to one side of a card whose width is set by the longest item above.
        // Centring costs nothing and needs no number: the pair sits in the middle of whatever width
        // the menu turns out to be.
        private static UIElement MakeButtonRow(Button ok, Button close)
        {
            close.Margin = new Thickness(10, 0, 0, 0);
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(8, 6, 8, 4)
            };
            row.Children.Add(ok);
            row.Children.Add(close);
            return row;
        }

        // The menu for a MULTI-ROW selection: opened by right-clicking a row that is part of it.
        //
        // Deliberately a different menu, not the single-row one with different wording. Edit and Search QRZ
        // are absent: they act on one contact, and offering them while seven rows are lit invites a click
        // that would quietly apply to only one of them. Every item here names the count, so what the menu
        // is about to touch is never in doubt.
        private ContextMenu BuildSelectionContextMenu(List<QSO> picked)
        {
            var res = CtxRes;
            var itemStyle = (Style)res["CtxItem"];
            var dangerStyle = (Style)res["CtxItemDanger"];
            var sepStyle = (Style)res["CtxSep"];
            var blue = (Brush)new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0));
            var red = (Brush)new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
            int n = picked.Count;

            var menu = new ContextMenu { Style = (Style)res["CtxMenu"] };

            // Title line: what the whole menu is talking about, with the first few callsigns under it so
            // the selection can be recognised without counting rows behind the menu.
            string names = string.Join(", ", picked.Take(6).Select(q => q.DXCall));
            if (n > 6) names += $", … (+{n - 6:N0} more)";
            menu.Items.Add(RowMenuParts.MakeMenuTitle($"{n:N0} QSOs selected", names));
            menu.Items.Add(new Separator { Style = sepStyle });

            var export = new MenuItem { Header = $"Export these {n:N0} to ADIF…", Style = itemStyle, Icon = MakeMenuGlyph("", blue) };
            // Deferred like Edit/Delete below: a modal file dialog opened while the menu is still
            // dismissing (and holding mouse capture) cannot take the click.
            export.Click += (s, e) => Dispatcher.BeginInvoke(new Action(() => ExportQsosToAdif(picked)),
                                                             System.Windows.Threading.DispatcherPriority.Background);
            menu.Items.Add(export);

            var clear = new MenuItem { Header = "Clear selection", Style = itemStyle, Icon = MakeMenuGlyph("", blue) };
            clear.Click += (s, e) => { ClearPicks(); UpdatePickState(); };
            menu.Items.Add(clear);

            var del = new MenuItem { Header = $"Delete these {n:N0} QSOs", Style = dangerStyle, Icon = MakeMenuGlyph("", red) };
            del.Click += (s, e) => Dispatcher.BeginInvoke(new Action(() => DeleteQsos(picked)),
                                                          System.Windows.Threading.DispatcherPriority.Background);
            menu.Items.Add(del);

            menu.Items.Add(new Separator { Style = sepStyle });

            var header = new MenuItem { Header = $"Send these {n:N0} to upload queue for:", Style = itemStyle, IsEnabled = false, Icon = MakeMenuGlyph("", blue) };
            menu.Items.Add(header);

            var s0 = Properties.Settings.Default;
            var cbLotw = RowMenuParts.MakeServiceCheck("LoTW", s0.UseLotwService);
            var cbQrz  = RowMenuParts.MakeServiceCheck("QRZ", s0.UseQrzLogbook);
            var cbEqsl = RowMenuParts.MakeServiceCheck("eQSL", s0.UseEqslService);
            var cbClub = RowMenuParts.MakeServiceCheck("Club Log", s0.UseClublogService);
            menu.Items.Add(RowMenuParts.MakeServiceGrid(cbLotw, cbQrz, cbEqsl, cbClub));

            var ok = new Button
            {
                Content = "OK",
                Width = 80,
                FontSize = 16,
                Padding = new Thickness(10, 4, 10, 4),
                Cursor = Cursors.Hand
            };
            ok.Click += (s, e) =>
            {
                QueueForUpload(picked, cbLotw.IsChecked == true, cbQrz.IsChecked == true,
                               cbEqsl.IsChecked == true, cbClub.IsChecked == true);
                menu.IsOpen = false;
            };
            menu.Items.Add(MakeButtonRow(ok, RowMenuParts.MakeCloseButton(menu)));

            return menu;
        }

        // Puts the QSO into the chosen services' upload queues (status 0 = pending). Always allowed - even
        // if already sent - so an edited QSO can be re-sent; the uploader reads the QSO's CURRENT fields.
        private void QueueForUpload(QSO qso, bool lotw, bool qrz, bool eqsl, bool club)
        {
            var dal = DataAccess.GetInstance();
            if (dal == null || qso == null) return;
            var done = new List<string>();
            try
            {
                if (lotw) { dal.SetLotwStatus(qso.id, 0);    done.Add("LoTW"); }
                if (qrz)  { dal.SetQrzStatus(qso.id, 0);     done.Add("QRZ"); }
                if (eqsl) { dal.SetEqslStatus(qso.id, 0);    done.Add("eQSL"); }
                if (club) { dal.SetClublogStatus(qso.id, 0); done.Add("Club Log"); }
            }
            catch (Exception ex) { Log.Swallow(ex); }

            if (done.Count > 0)
                HolyMessageBox.Show($"{qso.DXCall} queued for upload to: {string.Join(", ", done)}.",
                    "Upload queue", HolyMsgType.Info, this);
            else
                HolyMessageBox.Show("Tick at least one logger first.", "Upload queue", HolyMsgType.Info, this);
        }

        // The same thing for a whole selection. One message at the end rather than one per QSO - fifty
        // dialogs to dismiss would be its own kind of failure.
        private void QueueForUpload(List<QSO> qsos, bool lotw, bool qrz, bool eqsl, bool club)
        {
            if (qsos == null || qsos.Count == 0) return;
            if (!lotw && !qrz && !eqsl && !club)
            {
                HolyMessageBox.Show("Tick at least one logger first.", "Upload queue", HolyMsgType.Info, this);
                return;
            }

            var dal = DataAccess.GetInstance();
            if (dal == null) return;

            var done = new List<string>();
            if (lotw) done.Add("LoTW");
            if (qrz)  done.Add("QRZ");
            if (eqsl) done.Add("eQSL");
            if (club) done.Add("Club Log");

            int queued = 0, failed = 0;
            foreach (var q in qsos)
            {
                try
                {
                    if (lotw) dal.SetLotwStatus(q.id, 0);
                    if (qrz)  dal.SetQrzStatus(q.id, 0);
                    if (eqsl) dal.SetEqslStatus(q.id, 0);
                    if (club) dal.SetClublogStatus(q.id, 0);
                    queued++;
                }
                // One bad QSO must not abandon the rest half-queued, but it is counted and reported
                // rather than passed over in silence.
                catch (Exception ex) { failed++; Log.Swallow(ex); }
            }

            string message = $"{queued:N0} QSO{(queued == 1 ? "" : "s")} queued for upload to: {string.Join(", ", done)}.";
            if (failed > 0) message += $"\n\n{failed:N0} could not be queued.";
            HolyMessageBox.Show(message, "Upload queue", failed > 0 ? HolyMsgType.Warning : HolyMsgType.Info, this);
        }

        // Deleting a whole selection. One confirmation for the batch, and ONE undo step: undoing a
        // fifty-row delete fifty times over is not an undo the operator would trust.
        private void DeleteQsos(List<QSO> qsos)
        {
            if (qsos == null || qsos.Count == 0) return;
            if (qsos.Count == 1) { DeleteQso(qsos[0]); return; }

            try
            {
                // The confirmation names callsigns, not just a number: "delete 12 QSOs" is easy to agree
                // to without checking, and the ticks may have been made minutes ago.
                string preview = string.Join(", ", qsos.Take(10).Select(q => q.DXCall));
                if (qsos.Count > 10) preview += $", … (+{qsos.Count - 10:N0} more)";

                bool ok = HolyMessageBox.ShowConfirm(
                    $"Delete these {qsos.Count:N0} QSOs?\n\n{preview}\n\nYou can still undo it afterwards with the Undo button.",
                    "Delete QSOs", HolyMsgType.Warning, this);
                if (!ok) return;

                var dal = DataAccess.GetInstance();
                var bound = ResultsGrid.DataContext as ObservableCollection<QSO>;
                var deleted = new List<QSO>(qsos.Count);
                var logIds = new List<long>(qsos.Count);

                foreach (var q in qsos)
                {
                    long logId = dal?.GetQsoLogId(q.id) ?? -1;
                    dal?.Delete(q.id);
                    bound?.Remove(q);
                    _allQsos?.Remove(q);
                    q.IsPicked = false;
                    deleted.Add(q);
                    logIds.Add(logId);
                }

                try { ResultsGrid.Items.Refresh(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                UpdatePickState();

                _undo.Push(new EditStep
                {
                    IsDelete = true,
                    BatchQsos = deleted,
                    BatchLogIds = logIds,
                    Label = $"deleted {deleted.Count:N0} QSOs"
                });
                UpdateUndoButton();
                TB_Status.Text = $"Deleted {deleted.Count:N0} QSOs.  Press Undo to restore them.";
            }
            catch (Exception ex) { HolyMessageBox.ShowError("Could not delete the QSOs: " + ex.Message, "Delete QSOs", this); }
            finally { _hlKeep = false; ClearHighlight(); }
        }

        // Writes just the selected QSOs to an ADIF file, through the same generator the File menu's export
        // uses - so the selection exports with exactly the same fields and formats as a whole log would.
        private void ExportQsosToAdif(List<QSO> qsos)
        {
            if (qsos == null || qsos.Count == 0) return;
            try
            {
                // The carried ADIF fields are not loaded with the log - they are most of its weight and
                // no screen shows them - so they are fetched here for the QSOs being written out.
                try { DataAccess.GetInstance()?.FillCarriedAdif(qsos); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }

                string adif = HolyParser.Services.GenerateAdif(qsos, Contests.ContestService.Active?.CabrilloName,
                                                               includeImportedFields: true);
                var save = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "ADIF File|*.adi",
                    DefaultExt = "adi",
                    Title = $"Export {qsos.Count:N0} selected QSOs",
                    FileName = $"selection_{DateTime.Now:yyyyMMdd_HHmm}.adi"
                };
                if (save.ShowDialog() != true) return;

                System.IO.File.WriteAllText(save.FileName, adif);
                HolyMessageBox.ShowSuccess($"{qsos.Count:N0} QSO{(qsos.Count == 1 ? "" : "s")} exported.", "Export ADIF", this);
                TB_Status.Text = $"Exported {qsos.Count:N0} selected QSOs to {System.IO.Path.GetFileName(save.FileName)}.";
            }
            catch (Exception ex) { HolyMessageBox.ShowError("Export failed: " + ex.Message, "Export ADIF", this); }
        }

        private void EditQso(QSO qso, DataGridRow row)
        {
            try
            {
                Rect rect = default(Rect);
                try
                {
                    // The row's screen rectangle, converted to WPF (DIP) units so the editor can place
                    // itself above/below it correctly on high-DPI displays.
                    var src = PresentationSource.FromVisual(row);
                    if (row != null && src != null)
                    {
                        var m = src.CompositionTarget.TransformFromDevice;
                        Point tl = m.Transform(row.PointToScreen(new Point(0, 0)));
                        Point br = m.Transform(row.PointToScreen(new Point(row.ActualWidth, row.ActualHeight)));
                        rect = new Rect(tl, br);
                    }
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }

                var dlg = new QsoEditWindow(qso, rect) { Owner = this };
                bool? res = dlg.ShowDialog();
                if (res == true)
                {
                    try { ResultsGrid.Items.Refresh(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                }
            }
            finally { _hlKeep = false; ClearHighlight(); }
        }

        // Delete asks for confirmation first, and is ALSO undo-able (it goes on the same Undo stack as
        // edits) as a second safety net. The QSO's log is captured first so Undo restores it to the right log.
        private void DeleteQso(QSO qso)
        {
            try
            {
                bool ok = HolyMessageBox.ShowConfirm(
                    $"Delete this QSO with {qso.DXCall} on {qso.Date}?\n\nYou can still undo it afterwards with the Undo button.",
                    "Delete QSO", HolyMsgType.Warning, this);
                if (!ok) return;

                var dal = DataAccess.GetInstance();
                long logId = dal?.GetQsoLogId(qso.id) ?? -1;

                dal?.Delete(qso.id);
                (ResultsGrid.DataContext as ObservableCollection<QSO>)?.Remove(qso);
                _allQsos?.Remove(qso);
                try { ResultsGrid.Items.Refresh(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                UpdatePickState();   // a row just left the table; the count and header box must follow

                _undo.Push(new EditStep
                {
                    Qso = qso,
                    IsDelete = true,
                    LogId = logId,
                    Label = $"deleted {qso.DXCall} on {qso.Date}"
                });
                UpdateUndoButton();
                TB_Status.Text = $"Deleted {qso.DXCall}.  Press Undo to restore it.";
            }
            catch (Exception ex) { HolyMessageBox.ShowError("Could not delete the QSO: " + ex.Message, "Delete QSO", this); }
            finally { _hlKeep = false; ClearHighlight(); }
        }

        // logName names the log being searched, and is shown in the title bar. Any log can be searched
        // now - from the Log Manager, without activating it - so a window that just said "Log Search"
        // would leave you guessing whose QSOs you were about to edit.
        public SearchWindow(ObservableCollection<QSO> qsos, string logName = null)
        {
            InitializeComponent();
            _allQsos = qsos;

            string titleLog = string.IsNullOrWhiteSpace(logName) ? "(unnamed log)" : logName.Trim();
            TB_TitleLog.Text = titleLog;          // the bold half of the custom caption
            Title = "Log Workshop — " + titleLog;   // taskbar / Alt-Tab still use the plain Title

            // Build country list from distinct countries in the log, sorted A-Z
            _allCountries = _allQsos
                .Select(q => q.Country)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c)
                .Select(name => new SearchCountryItem(name))
                .ToList();

            _countriesView = new ListCollectionView(_allCountries);
            _countriesView.Filter = o =>
            {
                if (string.IsNullOrEmpty(_countryFilter)) return true;
                var item = (SearchCountryItem)o;
                int code; string name;
                ReadCountryText(_countryFilter, out code, out name);

                // Both are prefix matches, so 33 narrows to 336 (Israel) and everything else
                // beginning 33, exactly as Isr narrows to Israel.
                if (code > 0 && name.Length > 0) return item.Code == code;   // a country already picked
                if (code > 0) return item.CodeText.StartsWith(code.ToString(), StringComparison.Ordinal);
                return item.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase);
            };
            CB_Country.ItemsSource = _countriesView;

            // Attach TextChanged to the internal editable text box (for filter updates)
            CB_Country.Loaded += (sender, ev) =>
            {
                _countryEditBox = CB_Country.Template.FindName("PART_EditableTextBox", CB_Country) as TextBox;
                if (_countryEditBox != null)
                    _countryEditBox.TextChanged += OnCountryTextChanged;
            };

            // When the country dropdown is open, the ComboBox has mouse capture, so a normal
            // Click on the Clear button is swallowed by the popup-close (first click only
            // closes the list). This purpose-built event fires for a press outside the
            // captured element, letting us run Clear on that very first click.
            Mouse.AddPreviewMouseDownOutsideCapturedElementHandler(this, OnMouseDownOutsideCapture);

            PopulateFilterLists();
            WatchSourceForNewValues();
            UpdateClearButton();

            // Placement via the shared helper, like every other window. The bespoke code this replaces
            // saved on every LocationChanged/SizeChanged but restored without checking the position was
            // still on a monitor, and it never saved a window still open when the program closed.
            WindowBounds.Attach(this, "Search");

            // A correction to the last QSO touched would otherwise never be offered, because the row
            // is left by closing the window rather than by moving off it.
            Closing += (s, e) => OfferReupload();

            // The collection outlives this window (the main window owns it), so leaving the handler
            // attached would keep the closed window alive with it.
            Closed += (s, e) => { if (_allQsos != null) _allQsos.CollectionChanged -= OnSourceCollectionChanged; };

            // Keep the "Received Confirmation" overlay tracking the LoTW..Paper QSL header group's
            // actual on-screen bounds — column widths change (Auto-sizing) and the window resizes.
            ResultsGrid.Loaded += (s, e) => UpdateConfirmationStripPosition();
            ResultsGrid.LayoutUpdated += (s, e) => UpdateConfirmationStripPosition();

            // The same five columns drag as one block and admit no column between them.
            ConfirmationColumnGroup.Attach(ResultsGrid);

            // Where the operator put each column, and how wide they made it, kept between sessions - the
            // same as the main window's log table. See GridColumnLayout.
            ApplyWorkshopColumnLayout();
            ResultsGrid.ColumnDisplayIndexChanged += (s, e) => SaveWorkshopColumnLayout();
            // Widths can only be read on the way out: dragging a column divider raises no event we can
            // listen for, so this is the moment they are still true.
            Closed += (s, e) =>
            {
                SaveWorkshopColumnLayout();
                try { Properties.Settings.Default.Save(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
            };
        }

        // Guards, for the same reasons as the main window's: nothing is written back while the saved
        // layout is being applied, and nothing at all until it HAS been - WPF raises
        // ColumnDisplayIndexChanged while it is still assigning the XAML's own indexes, and saving then
        // would put the default arrangement over the operator's.
        private bool _applyingWorkshopColumnLayout;
        private bool _workshopColumnLayoutApplied;

        private void SaveWorkshopColumnLayout()
        {
            if (_applyingWorkshopColumnLayout || !_workshopColumnLayoutApplied || ResultsGrid == null) return;
            try
            {
                Properties.Settings.Default.WorkshopColumnLayout = GridColumnLayout.Capture(ResultsGrid);
                SettingsFlush.RequestSave();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        private void ApplyWorkshopColumnLayout()
        {
            try
            {
                _applyingWorkshopColumnLayout = true;
                GridColumnLayout.Apply(ResultsGrid, Properties.Settings.Default.WorkshopColumnLayout);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            finally
            {
                _applyingWorkshopColumnLayout = false;
                _workshopColumnLayoutApplied = true;   // from here on, a drag or a resize is the operator's
            }
        }

        private Rect _confirmationStripLastRect = Rect.Empty;

        private void UpdateConfirmationStripPosition()
        {
            ConfirmationStripHelper.UpdatePosition(ResultsGrid, ConfirmationStripLabel, ref _confirmationStripLastRect,
                "LotwStatusRank", "PaperQslStatusRank");
        }

        private void OnMouseDownOutsideCapture(object sender, MouseButtonEventArgs e)
        {
            if (!CB_Country.IsDropDownOpen) return;

            // Did the press land on the Clear button? If so, clear on this first click.
            Point p = e.GetPosition(Btn_Clear);
            if (p.X >= 0 && p.Y >= 0 && p.X <= Btn_Clear.ActualWidth && p.Y <= Btn_Clear.ActualHeight)
                ClearAll();
        }

        // Pre-fills the callsign boxes (used when opened from a log-row right-click). The callsign is
        // split into the same two halves the boxes hold, so arriving here from the log shows how that
        // callsign breaks down rather than dropping it whole into one box.
        public void SetCallsign(string call, bool runSearch = false)
        {
            CallsignIdentity.Split((call ?? string.Empty).Trim().ToUpperInvariant(),
                                   out string prefix, out string suffix);
            TB_Prefix.Text = prefix;
            TB_Suffix.Text = suffix;
            TB_Suffix.CaretIndex = TB_Suffix.Text.Length;
            UpdateClearButton();
            TB_Prefix.Focus();
            if (runSearch)
                RunSearch();
        }

        // Pre-fills the Country box and (optionally) runs the search (used when opened from a country
        // row in the Statistics window). Clears the callsign so it is a pure country search.
        public void SetCountry(string country, bool runSearch = false)
        {
            TB_Prefix.Text = "";
            TB_Suffix.Text = "";
            CB_Country.IsDropDownOpen = false;
            CB_Country.SelectedItem = null;   // before Text= so WPF doesn't fight the assignment
            CB_Country.Text = (country ?? string.Empty).Trim();
            _countryFilter = CB_Country.Text;
            UpdateClearButton();
            if (runSearch)
                RunSearch();
        }

        // The Country box has two faces.
        //
        // TYPING face: an ordinary editable text box, where a name or an entity number is entered.
        // PICKED face: the chosen row itself - number, flag, name - which only a NON-editable
        // ComboBox can draw, because an editable one is a text box and text has no flag in it.
        //
        // Both faces leave ComboBox.Text holding "336  Israel", which is the only thing the search
        // reads, so switching between them never changes what a search finds.
        private void ShowCountryAsPicked(SearchCountryItem item)
        {
            if (item == null) return;
            _countryFilter = "";
            _countriesView?.Refresh();
            if (CB_Country.IsEditable) CB_Country.IsEditable = false;
            CB_Country.SelectedItem = item;
            UpdateClearButton();
        }

        // Back to the typing face, carrying `text` into the box (empty to clear it).
        private void ReturnCountryToTyping(string text)
        {
            if (!CB_Country.IsEditable)
            {
                CB_Country.IsEditable = true;
                // The editable text box is recreated when IsEditable flips back on; re-hook the
                // type-to-filter handler to the new instance.
                CB_Country.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        CB_Country.ApplyTemplate();
                        var box = CB_Country.Template.FindName("PART_EditableTextBox", CB_Country) as TextBox;
                        if (box != null)
                        {
                            _countryEditBox = box;
                            _countryEditBox.TextChanged -= OnCountryTextChanged;
                            _countryEditBox.TextChanged += OnCountryTextChanged;
                            // The caret belongs after what is already there, so the next character
                            // extends the text instead of replacing it.
                            box.CaretIndex = box.Text.Length;
                        }
                    }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }

            CB_Country.SelectedItem = null;
            CB_Country.Text = text ?? "";
            if (_countryEditBox != null) _countryEditBox.Text = text ?? "";
            _countryFilter = text ?? "";
            _countriesView?.Refresh();
        }

        // The Country box can hold three things: a name ("Israel"), an entity number being typed
        // ("33"), or the two together as the list writes them once a country is picked ("336  Israel").
        // This is the ONE reader for all three, so the dropdown's filter and the search itself can
        // never disagree about what the operator meant.
        //
        // No country name begins with a digit, so leading digits are always a number and never part
        // of a name.
        private static void ReadCountryText(string typed, out int code, out string name)
        {
            code = 0;
            name = (typed ?? string.Empty).Trim();
            if (name.Length == 0) return;

            int digits = 0;
            while (digits < name.Length && name[digits] >= '0' && name[digits] <= '9') digits++;
            if (digits == 0) return;                       // starts with a letter: a plain name

            int parsed;
            if (!int.TryParse(name.Substring(0, digits), out parsed) || parsed <= 0) return;

            code = parsed;
            name = name.Substring(digits).Trim();          // empty when only digits were typed
        }

        // Keep filter in sync whenever text changes (from typing or selection)
        private void OnCountryTextChanged(object sender, TextChangedEventArgs e)
        {
            // Only GENUINE typing drives the filter. Highlighting or clicking a row makes WPF rewrite
            // the edit box to that row's own text ("336  Israel"), and letting that drive the filter
            // caused two faults: arrowing collapsed the browsed list down to the one highlighted row,
            // and — because a leading number is read as an entity code — picking a country then left
            // the filter stuck on that country ("336"), so after clearing the box every keystroke
            // still returned only Israel. Detect the rewrite by content, not timing (a held arrow key
            // was unreliable and never covered the mouse): if the text equals the selected row's text,
            // it wasn't typed, so leave the filter alone.
            var selected = CB_Country.SelectedItem as SearchCountryItem;
            if (selected != null && string.Equals(CB_Country.Text, selected.ToString(), StringComparison.Ordinal))
                return;

            _countryFilter = CB_Country.Text;
            _countriesView.Refresh();
            UpdateClearButton();
        }

        // Enter before the ComboBox processes it → search (only when dropdown is closed)
        private void CB_Country_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Enter while the list is open commits the country the Up/Down highlight is on (moving the
            // highlight no longer picks on its own — that is exactly the change this fixes). With
            // nothing highlighted, Enter just closes the list, leaving the typed text to search on the
            // next Enter, as it did before.
            if (e.Key == Key.Enter && CB_Country.IsDropDownOpen)
            {
                var highlighted = CB_Country.SelectedItem as SearchCountryItem;
                CB_Country.IsDropDownOpen = false;
                if (highlighted != null) ShowCountryAsPicked(highlighted);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter && !CB_Country.IsDropDownOpen)
            {
                RunSearch();
                e.Handled = true;
                return;
            }

            // Typing over a picked country: the flag row is a display, not a lock. Any character
            // turns the box back into a text box and becomes its first character - carried across by
            // hand, because the text box does not exist yet at this instant and the keystroke would
            // otherwise be swallowed by the switch.
            if (!CB_Country.IsEditable)
            {
                string typed = TypedCharacter(e.Key);
                if (typed != null)
                {
                    ReturnCountryToTyping(typed);
                    CB_Country.IsDropDownOpen = true;
                    UpdateClearButton();
                    e.Handled = true;
                }
                else if (e.Key == Key.Back || e.Key == Key.Delete)
                {
                    ReturnCountryToTyping("");
                    UpdateClearButton();
                    e.Handled = true;
                }
            }
        }

        // The character a key stands for, or null when the key types nothing (arrows, F-keys,
        // modifiers). Only what a country name or an entity number can be made of.
        private static string TypedCharacter(Key key)
        {
            if (Keyboard.Modifiers != ModifierKeys.None && Keyboard.Modifiers != ModifierKeys.Shift) return null;
            if (key >= Key.A && key <= Key.Z) return ((char)('A' + (key - Key.A))).ToString();
            if (key >= Key.D0 && key <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
            if (key >= Key.NumPad0 && key <= Key.NumPad9) return ((char)('0' + (key - Key.NumPad0))).ToString();
            return null;
        }

        // True while an Up/Down (or Page Up/Down) key is physically held — i.e. the selection change
        // now firing was caused by moving the highlight through the open list, not by a mouse click or
        // Enter. Reading the live key state needs no flag to set and clear, so a keystroke that moves
        // nothing (already at the end of the list) can't leave a stale "suppress" behind to swallow
        // the next mouse click.
        private static bool IsListNavKeyDown() =>
            Keyboard.IsKeyDown(Key.Down) || Keyboard.IsKeyDown(Key.Up) ||
            Keyboard.IsKeyDown(Key.PageDown) || Keyboard.IsKeyDown(Key.PageUp);

        // Picking a row from the list shows that row - number, flag, name - in the closed box.
        // Moving the highlight with the keyboard must NOT pick: selection is committed only by a mouse
        // click or by Enter (handled in PreviewKeyDown). So ignore the selection change while an arrow
        // key is driving it, leaving the list open for the operator to keep browsing.
        private void CB_Country_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IsListNavKeyDown()) return;

            var picked = CB_Country.SelectedItem as SearchCountryItem;
            if (picked != null) ShowCountryAsPicked(picked);

            // Picking a country with the mouse fires no keystroke, so nothing else here would notice
            // that a filter has just been set - and Search would stay grey over a chosen country.
            UpdateClearButton();
        }

        // KeyUp bubbles up from the internal text box AFTER the text is already updated.
        // Open the filtered dropdown for any printable/editing key; skip navigation/modifier keys.
        // Mouse-click selections never fire KeyUp, so the dropdown won't reopen after a selection.
        private void CB_Country_KeyUp(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                case Key.Escape:
                case Key.Tab:
                case Key.Up:
                case Key.Down:
                case Key.Left:
                case Key.Right:
                case Key.LeftCtrl:   case Key.RightCtrl:
                case Key.LeftShift:  case Key.RightShift:
                case Key.LeftAlt:    case Key.RightAlt:
                case Key.LWin:       case Key.RWin:
                    return;
            }

            CB_Country.IsDropDownOpen = !string.IsNullOrEmpty(CB_Country.Text);

            // The country box is a ComboBox, not a TextBox, so no TextChanged brings this on. Typing
            // into it - and rubbing it out again - has to move the Search button like every other box.
            UpdateClearButton();
        }

        // What to do with a keystroke when the whole edit box is selected depends on WHAT is selected:
        //
        //  - The in-progress typed filter. Opening the dropdown makes WPF auto-select the whole box, so
        //    the next character would REPLACE it and the first letter vanished when typing fast (type
        //    "I", it selects "I", type "s" → "s" not "Is"). Collapse the selection to the caret so the
        //    character APPENDS. Nothing is committed here, so SelectedItem is null.
        //
        //  - A committed country ("336  Israel", SelectedItem set). The operator is starting over, so
        //    the keystroke should REPLACE the whole thing and begin a fresh list from that one
        //    character — the standard behaviour of typing into a fully selected field. Leave the
        //    selection alone and let WPF replace it.
        //
        // Runs synchronously right before each character commits, so it works at any typing speed.
        private void CB_Country_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var tb = _countryEditBox;
            if (tb == null || tb.SelectionLength == 0 || tb.SelectionLength != tb.Text.Length)
                return;

            var selected = CB_Country.SelectedItem as SearchCountryItem;
            bool holdsCommittedCountry = selected != null &&
                string.Equals(tb.Text, selected.ToString(), StringComparison.Ordinal);
            if (holdsCommittedCountry)
            {
                // Start a brand-new list from this one character. Leaning on WPF's own "replace the
                // selection" fought the ComboBox's text↔SelectedItem sync: it blanked the box and
                // swallowed the first keystroke, so the list only appeared on the second. Do the reset
                // ourselves — drop the picked country and seed the filter with this character — and
                // mark the keystroke handled so WPF doesn't also insert it.
                e.Handled = true;
                ReturnCountryToTyping(e.Text);
                tb.CaretIndex = tb.Text.Length;
                CB_Country.IsDropDownOpen = true;
                return;
            }

            tb.SelectionStart  = tb.Text.Length;
            tb.SelectionLength = 0;
        }

        // Callsign box: clear results immediately when text is fully deleted
        // Typing only lights up the Clear button. The results are left alone until Search / Enter, so
        // deleting the last character of a callsign does not throw the whole log back at you mid-edit.
        private void SearchField_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateClearButton();
        }

        private static readonly EntityResolver _entityResolver = new EntityResolver();

        // Typing a callsign prefix implies the country, so auto-fill the Country box from it and LOCK the
        // Country dropdown - you can't pick a country that contradicts the prefix - mirroring how a
        // frequency drives and locks the band. Clearing the prefix re-enables and empties the Country box.
        private void Prefix_TextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                bool prefixFilled = !string.IsNullOrWhiteSpace(TB_Prefix.Text);

                // The continent is implied by the callsign too, so it follows the same rule as the country:
                // derived from the prefix and locked while one is typed.
                if (CB_Continent != null) CB_Continent.IsEnabled = !prefixFilled;
                if (prefixFilled)
                {
                    string cont = CountryLookup.Shared.Resolve(
                        (TB_Prefix.Text + (TB_Suffix != null ? TB_Suffix.Text : string.Empty)).Trim())?.Continent;
                    if (CB_Continent != null)
                    {
                        object hit = null;
                        if (!string.IsNullOrWhiteSpace(cont))
                            foreach (var it in CB_Continent.Items)
                                if (string.Equals(it as string, cont, StringComparison.OrdinalIgnoreCase)) { hit = it; break; }
                        CB_Continent.SelectedItem = hit;
                        if (hit == null && CB_Continent.Items.Count > 0) CB_Continent.SelectedIndex = 0;
                    }
                }
                else if (CB_Continent != null && CB_Continent.Items.Count > 0)
                {
                    CB_Continent.SelectedIndex = 0;   // prefix cleared -> drop the derived continent
                }

                if (prefixFilled)
                {
                    string call = (TB_Prefix.Text + (TB_Suffix != null ? TB_Suffix.Text : string.Empty)).Trim();
                    string country = CountryLookup.Shared.Resolve(call)?.Name;
                    var item = (!string.IsNullOrEmpty(country) && !string.Equals(country, "Unknown", StringComparison.OrdinalIgnoreCase))
                               ? _allCountries.FirstOrDefault(c => string.Equals(c.Name, country, StringComparison.OrdinalIgnoreCase))
                               : null;
                    if (item != null)
                    {
                        // Show the flag + name exactly as in the list: a NON-editable combo renders the
                        // selected item through the flag+name ItemTemplate.
                        _countryFilter = "";
                        _countriesView?.Refresh();
                        CB_Country.IsEditable = false;
                        CB_Country.SelectedItem = item;
                    }
                    else
                    {
                        // Country not in this log's list (never worked) - just show its name as text.
                        CB_Country.IsEditable = true;
                        CB_Country.SelectedItem = null;
                        CB_Country.Text = country ?? "";
                    }
                    CB_Country.IsEnabled = false;
                }
                else
                {
                    CB_Country.IsEnabled = true;
                    ReturnCountryToTyping("");
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            UpdateClearButton();
        }

        // Typing a frequency auto-selects its band in the Band filter (same freq->band rule as the QSO
        // editor), so a frequency search is also a band search. Only selects a band the log actually has
        // (the Band list is built from the log); otherwise the band is left as-is.
        private void Freq_TextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                bool freqFilled = !string.IsNullOrWhiteSpace(TB_Freq.Text);
                // Band is derived from the frequency and enforced-by-frequency, so LOCK the Band dropdown
                // while a frequency is present; re-enable it (and clear the derived value) when empty.
                if (CB_Band != null) CB_Band.IsEnabled = !freqFilled;

                if (freqFilled)
                {
                    string mhz = HolyLogParser.NormalizeFreqToMhz(TB_Freq.Text.Trim());
                    if (!string.IsNullOrWhiteSpace(mhz))
                    {
                        string band = HolyLogParser.convertFreqToBand(mhz);
                        if (!string.IsNullOrWhiteSpace(band) && CB_Band != null)
                            foreach (var item in CB_Band.Items)
                                if (string.Equals(item as string, band, StringComparison.OrdinalIgnoreCase))
                                {
                                    CB_Band.SelectedItem = item;
                                    break;
                                }
                    }
                }
                else if (CB_Band != null && CB_Band.Items.Count > 0)
                {
                    CB_Band.SelectedIndex = 0;   // frequency cleared -> drop the derived band filter
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            UpdateClearButton();
        }

        private void SearchField_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                RunSearch();
        }

        // Put the caret in the Callsign box as soon as the window opens so the user can type
        // a callsign immediately.
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Ctrl+C: tell every template column what it stands for, or the copy is headings only.
            GridCopy.Enable(ResultsGrid);

            // Prefix holds 6 characters, suffix 10 - measured rather than guessed in pixels, so the
            // boxes stay right if the theme font or size ever changes.
            SizeToCharacters(TB_Prefix, 6);
            SizeToCharacters(TB_Suffix, 10);

            // This was far wider than anything it can hold: sized to a real worst-case value, a
            // six-character grid square, instead of a round number.
            SizeToSample(TB_Locator, "KM72OR");

            // Sizes the dropdowns to this log's values and re-aligns the Comment box behind them.
            SizeFilterListsToContent();

            // The filter rows (Prefix/Suffix/.../Submode, and the rest below) must never be allowed to
            // clip or wrap - so the window can't be resized narrower than what they actually need. Rather
            // than hard-code a pixel guess, measure the real rendered width once everything above has
            // settled, and raise MinWidth (and the current Width, if it is currently smaller) to match.
            Dispatcher.BeginInvoke(new Action(LockMinWidthToContent), System.Windows.Threading.DispatcherPriority.Loaded);

            // Open showing the whole log, so the window starts as a view OF the log rather than a
            // blank form. Filters then narrow it down.
            RunSearch();

            // Search starts grey and dead, because nothing is filled in yet. Without this the window
            // opened with a blue Search button over a form with no filter in it.
            UpdateClearButton();

            TB_Prefix.Focus();
            Keyboard.Focus(TB_Prefix);
        }

        // Sizes each log-built dropdown to the longest value THIS log put in it, rather than to a fixed
        // width that has to assume the worst. Re-run whenever the lists are rebuilt, so the boxes follow
        // the log that is loaded: a log of 4X callsigns should not keep a box sized for 4X2XMAS.
        //
        // "(any)" is itself one of the items, so a log with nothing in the field still gets a box wide
        // enough to read it. maxWidth keeps one freakish value from pushing the row past the window.
        private void SizeFilterListsToContent()
        {
            SizeToSample(CB_MyCall, LongestItem(CB_MyCall), dropDownArrow);
            SizeComboToSample(CB_Band,    LongestItem(CB_Band),    maxWidth: 68);
            SizeComboToSample(CB_Mode,    LongestItem(CB_Mode),    maxWidth: 68);
            SizeComboToSample(CB_Submode, LongestItem(CB_Submode), maxWidth: 92);
            SizeComboToSample(CB_CqZone,  LongestItem(CB_CqZone),  maxWidth: 62);
            SizeComboToSample(CB_ItuZone, LongestItem(CB_ItuZone), maxWidth: 62);
            SizeComboToSample(CB_State,   LongestItem(CB_State),   maxWidth: 80);
            SizeComboToSample(CB_Square,  LongestItem(CB_Square),  maxWidth: 92);

            // Last: it measures where Band and Submode ended up, so they must already be their final
            // size. Re-run here rather than only at load, because a rebuild can change those widths.
            AlignCommentBox();
        }

        // Raises MinWidth (and Width, if it is currently smaller) to whatever the filter rows actually
        // need, measured from the real rendered layout rather than a guessed pixel number - so it stays
        // correct if the theme font, DPI, or the filters themselves ever change. +20 is the search bar
        // Border's own Padding="10,8" (left+right); +8 is a small rounding safety margin.
        private void LockMinWidthToContent()
        {
            try
            {
                if (FiltersPanel == null || !FiltersPanel.IsArrangeValid) return;
                double required = FiltersPanel.ActualWidth + 20 + 8;
                if (required > MinWidth) MinWidth = required;
                if (ActualWidth < MinWidth) Width = MinWidth;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Esc anywhere in the window clears both fields and the results (same as the Clear
        // button). PreviewKeyDown tunnels in before the ComboBox can swallow Esc to merely
        // close its dropdown, so Esc always performs the full clear.
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+Z undoes a SAVED edit - but only where it would otherwise do nothing. Inside a text
            // box it still means "undo my typing", which is what the operator expects there, and inside
            // an open cell editor the same applies.
            if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (_cellInEdit || Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)
                    return;
                UndoLastEdit();
                e.Handled = true;
                return;
            }

            if (e.Key != Key.Escape) return;

            // While a cell is being edited, Esc belongs to the DataGrid: it abandons the change and
            // puts the logged value back. This handler tunnels in FIRST, so without this guard Esc
            // would wipe every filter instead - and rebuild the grid underneath a row that was still
            // in edit. Cancelling an edit is what Esc means at that moment.
            if (_cellInEdit) return;

            // AND ESC DOES WHAT THE CLEAR BUTTON DOES - INCLUDING NOTHING. With no filter set the button
            // is dead, so the key must be dead too, or the same window answers the same request two
            // different ways. Left unhandled here it also stays available to whatever else wants it.
            if (!Btn_Clear.IsEnabled) return;

            ClearAll();
            e.Handled = true;
        }

        private void Btn_Search_Click(object sender, RoutedEventArgs e) => RunSearch();

        private void Btn_Clear_Click(object sender, RoutedEventArgs e) => ClearAll();

        // Paper QSL checkbox toggled in the search results. The two-way binding has already updated the
        // QSO; persist it and tell the Statistics window (if open) so its Paper QSL folder recomputes.
        // Attached at the DataGrid level (CheckBox.Checked/Unchecked="PaperQsl_Changed" on ResultsGrid, in
        // the XAML), so this fires for the shared PaperQslTemplate's checkbox via routed-event bubbling -
        // that template has no handler of its own, since it is shared with the main window's grid too.
        // Bubbling means `sender` is the DataGrid the handler is registered on, NOT the checkbox that was
        // actually clicked - e.OriginalSource is the one that raised it.
        private void PaperQsl_Changed(object sender, RoutedEventArgs e)
        {
            var box = e.OriginalSource as CheckBox;
            // The selection column's boxes bubble through here too, and their DataContext is a QSO just
            // the same - without this a tick in the selection column would write a paper QSL to the log.
            if ((box?.Tag as string) == "PickBox") return;
            if (!(box?.DataContext is QSO qso)) return;
            try
            {
                DataAccess.GetInstance()?.SetPaperQslConfirmed(qso.id, qso.PaperQslConfirmed);
                var stats = Application.Current?.Windows.OfType<StatisticsWindow>().FirstOrDefault();
                if (stats != null && stats.IsLoaded) stats.NotifyPaperQslChanged(qso.id, qso.PaperQslConfirmed);
            }
            catch (Exception ex) { Log.Swallow(ex); }
        }

        // ===== Row selection: the tick boxes AND the highlight, which are one thing =====
        //
        // The boxes were once the ONLY thing that selected - modelled on a mail client, so that a
        // selection of eighty rows could not be wiped by one stray click. The operator asked for the
        // plainer arrangement instead: clicking a row highlights it and ticks it, Ctrl+click adds
        // another, Shift+click takes the range, and Esc clears the lot. QSO.IsPicked is still the state
        // the menus read; the two are kept in step in both directions.

        private int _lastPickedIndex = -1;   // Shift+click anchor, as an index into the rows on show
        private bool _syncingPickAll;        // set while WE write the header box, so its handler stays quiet

        // Ticks -> highlight. The other direction is ResultsGrid_SelectionChanged; this one is for the
        // places that set IsPicked directly - a Shift+click across the boxes, the header's tick-all -
        // where the rows must light up to match or the table contradicts itself.
        private void SyncSelectionFromPicks()
        {
            try
            {
                _syncingPicks = true;
                ResultsGrid.UnselectAll();
                foreach (QSO q in ResultsGrid.Items.OfType<QSO>())
                    if (q.IsPicked) ResultsGrid.SelectedItems.Add(q);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            finally { _syncingPicks = false; }
        }

        private void PickBox_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var box = sender as CheckBox;
                if (box == null) return;

                bool ticked = box.IsChecked == true;   // the binding has already written this to the QSO
                int index = ResultsGrid.Items.IndexOf(box.DataContext);

                // Shift+click fills in everything between the box clicked last and this one, both ends
                // included - the alternative is ticking eighty rows one at a time.
                if (index >= 0 && _lastPickedIndex >= 0 && _lastPickedIndex < ResultsGrid.Items.Count &&
                    (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                {
                    int from = Math.Min(_lastPickedIndex, index);
                    int to   = Math.Max(_lastPickedIndex, index);
                    for (int i = from; i <= to; i++)
                        if (ResultsGrid.Items[i] is QSO q) q.IsPicked = ticked;
                }

                _lastPickedIndex = index;
                SyncSelectionFromPicks();   // the rows light up to match the boxes
                UpdatePickState();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // The box in the column header. Nothing ticked -> tick every row the search is showing; anything
        // ticked -> clear the lot. Only the listed rows are touched, never the whole log.
        private void Chk_PickAll_Click(object sender, RoutedEventArgs e)
        {
            if (_syncingPickAll) return;
            try
            {
                bool tickAll = !ResultsGrid.Items.OfType<QSO>().Any(q => q.IsPicked);
                foreach (var q in ResultsGrid.Items.OfType<QSO>()) q.IsPicked = tickAll;
                _lastPickedIndex = -1;
                SyncSelectionFromPicks();   // every row lights up, or none does
                UpdatePickState();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Brings the header box and the status-bar count back in line with what is actually ticked.
        private void UpdatePickState()
        {
            try
            {
                int total = 0, picked = 0;
                foreach (var q in ResultsGrid.Items.OfType<QSO>())
                {
                    total++;
                    if (q.IsPicked) picked++;
                }

                _syncingPickAll = true;
                // null = the filled square: some rows ticked, but not all of them.
                Chk_PickAll.IsChecked = picked == 0 ? (bool?)false : picked == total ? (bool?)true : null;
                _syncingPickAll = false;

                TB_PickCount.Text = picked == 1 ? "1 selected" : $"{picked:N0} selected";
                TB_PickCount.Visibility = picked == 0 ? Visibility.Collapsed : Visibility.Visible;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // A new search lists a different set of rows, so the old ticks mean nothing. Cleared across the
        // WHOLE log rather than just the rows on screen: the state sits on the QSOs, which outlive the
        // result list, so a row ticked in an earlier search would otherwise come back still ticked.
        private void ClearPicks()
        {
            if (_allQsos != null)
                foreach (var q in _allQsos) q.IsPicked = false;
            _lastPickedIndex = -1;

            // AND THE HIGHLIGHT WITH THEM. The ticks and the highlight are one thing now, so clearing
            // one and leaving the other is the program disagreeing with itself: Esc emptied every box
            // and left the rows still lit up, which reads as "something is still selected" - and after
            // the change above, something WOULD still have been.
            try
            {
                _syncingPicks = true;                 // the deselect must not be turned back into ticks
                ResultsGrid?.UnselectAll();
                ResultsGrid?.UnselectAllCells();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            finally { _syncingPicks = false; }
        }

        private void ClearAll()
        {
            TB_Prefix.Text          = "";
            TB_Suffix.Text          = "";
            CB_Country.IsDropDownOpen = false;
            // BACK TO THE TYPING FACE, not merely emptied. The Country box wears two faces: an editable
            // one for typing, and a plain one that can draw the flag beside the name once a country has
            // been picked. Picking one turns IsEditable off - and clearing used only to blank the text,
            // so the box kept the picked face: grey and unlike every other box on the row, over nothing
            // at all. This is the one call that empties it AND puts its face back.
            ReturnCountryToTyping("");

            // Clear means clear EVERYTHING, including the second row of filters - otherwise a band or
            // date left set from the previous search silently narrows the next one.
            if (CB_Band.Items.Count > 0)   CB_Band.SelectedIndex = 0;
            if (CB_Mode.Items.Count > 0)   CB_Mode.SelectedIndex = 0;
            if (CB_MyCall.Items.Count > 0) CB_MyCall.SelectedIndex = 0;
            if (CB_Lotw.Items.Count > 0)   CB_Lotw.SelectedIndex = 0;
            TB_Locator.Text = "";
            TB_Comment.Text = "";
            DP_From.SelectedDate = null;
            DP_To.SelectedDate   = null;
            // The rest of the fields added so every QSO field is searchable.
            TB_Name.Text = ""; TB_Operator.Text = ""; TB_Freq.Text = "";
            TB_MyGrid.Text = ""; TB_MySquare.Text = "";
            TB_PropMode.Text = ""; TB_SatName.Text = ""; TB_Soapbox.Text = "";
            TB_Time.Text = ""; TB_Qth.Text = "";
            // The dropdowns built from the log: back to "(any)".
            foreach (var cb in new[] { CB_Submode, CB_CqZone, CB_ItuZone, CB_State, CB_Square, CB_Continent })
                if (cb.Items.Count > 0) cb.SelectedIndex = 0;
            if (CB_Qrz.Items.Count > 0)     CB_Qrz.SelectedIndex = 0;
            if (CB_Eqsl.Items.Count > 0)    CB_Eqsl.SelectedIndex = 0;
            if (CB_Clublog.Items.Count > 0) CB_Clublog.SelectedIndex = 0;
            if (CB_Paper.Items.Count > 0)   CB_Paper.SelectedIndex = 0;
            if (CB_Review.Items.Count > 0)  CB_Review.SelectedIndex = 0;

            // Back to the whole log, not to an empty grid - clearing a filter should reveal everything
            // again, exactly as removing a spreadsheet filter does.
            RunSearch();
            UpdateClearButton();
            TB_Prefix.Focus();
        }

        // Blue while any filter is set (so it's clearly clickable), gray when everything is empty.
        //
        // SEARCH FOLLOWS THE SAME RULE, and is switched off as well as greyed. With nothing filled in
        // there is nothing to search for: the window already shows the whole log, and pressing Search
        // redrew the same rows and looked like a button that did nothing. Grey and dead until the first
        // filter is set, then blue - so the colour coming up is itself the sign that the box just filled
        // in has been noticed.
        private void UpdateClearButton()
        {
            bool hasContent = !string.IsNullOrEmpty(TB_Prefix.Text) ||
                              !string.IsNullOrEmpty(TB_Suffix.Text) ||
                              !string.IsNullOrEmpty(CB_Country.Text) ||
                              !string.IsNullOrEmpty(TB_Locator.Text) ||
                              SelectedFilter(CB_Square) != null ||
                              !string.IsNullOrEmpty(TB_Comment.Text) ||
                              SelectedFilter(CB_Band) != null ||
                              SelectedFilter(CB_Mode) != null ||
                              SelectedFilter(CB_MyCall) != null ||
                              SelectedFilter(CB_Lotw) != null ||
                              DP_From.SelectedDate != null ||
                              DP_To.SelectedDate != null ||
                              !string.IsNullOrEmpty(TB_Name.Text) ||
                              !string.IsNullOrEmpty(TB_Operator.Text) ||
                              !string.IsNullOrEmpty(TB_Freq.Text) ||
                              SelectedFilter(CB_Submode) != null ||
                              !string.IsNullOrEmpty(TB_MyGrid.Text) ||
                              !string.IsNullOrEmpty(TB_MySquare.Text) ||
                              SelectedFilter(CB_CqZone) != null ||
                              SelectedFilter(CB_ItuZone) != null ||
                              SelectedFilter(CB_Continent) != null ||
                              !string.IsNullOrEmpty(TB_PropMode.Text) ||
                              !string.IsNullOrEmpty(TB_SatName.Text) ||
                              !string.IsNullOrEmpty(TB_Soapbox.Text) ||
                              !string.IsNullOrEmpty(TB_Time.Text) ||
                              !string.IsNullOrEmpty(TB_Qth.Text) ||
                              SelectedFilter(CB_State) != null ||
                              SelectedFilter(CB_Qrz) != null ||
                              SelectedFilter(CB_Eqsl) != null ||
                              SelectedFilter(CB_Clublog) != null ||
                              SelectedFilter(CB_Paper) != null ||
                              SelectedFilter(CB_Review) != null;
            // SWITCHED OFF, NOT ONLY GREYED. With no filter set there is nothing to clear, and a button
            // that can be pressed to no effect is a button that teaches the operator to distrust the
            // window. Worse, the two greys had come to mean different things - Search's grey was dead
            // and Clear's grey was alive, in the same shade, side by side.
            Btn_Clear.IsEnabled = hasContent;
            Btn_Clear.Background = hasContent ? ClearActiveBrush : ClearIdleBrush;

            if (Btn_Search != null)
            {
                Btn_Search.IsEnabled = hasContent;
                Btn_Search.Background = hasContent ? SearchActiveBrush : ClearIdleBrush;
                Btn_Search.BorderBrush = hasContent ? SearchBorderBrush : ClearIdleBrush;
            }
        }

        // Choices offered by the Band and Mode cell dropdowns. Deliberately the SAME lists the
        // Bad-QSO editor uses, so the two places that repair a QSO cannot offer different vocabularies.
        //
        // The bands now come from the one list in HolyParser rather than a copy of it. The copy had
        // quietly fallen behind - no 4M, no 23CM - and a QSO on a band this list did not know left the
        // cell's dropdown with nothing selected. Kept under this name because the grid's editing
        // template binds to it by x:Static.
        public static readonly string[] KnownBands = HolyLogParser.KnownBands;

        public static readonly string[] KnownModes =
        {
            "SSB", "USB", "LSB", "CW", "FM", "RTTY", "FT8", "FT4", "PSK31", "DIGI"
        };

        // The full official ADIF Submode enumeration (adif.org). The Submode filter used to be built
        // from this, so it offered every value the standard defines; it is now built from the log like
        // every other dropdown, and nothing else reads this list. Kept because it is the authoritative
        // spelling of each submode, and the QSO editor's free-text Submode box is the obvious next
        // place for it.
        public static readonly string[] KnownSubmodes =
        {
            "8PSK125", "8PSK125F", "8PSK125FL", "8PSK250", "8PSK250F", "8PSK250FL",
            "8PSK500", "8PSK500F", "8PSK1000", "8PSK1000F", "8PSK1200F",
            "AMTORFEC", "GTOR", "NAVTEX", "SITORB",
            "CHIP64", "CHIP128",
            "PCW",
            "C4FM", "DMR", "DSTAR", "FREEDV", "M17",
            "DOM-M", "DOM4", "DOM5", "DOM8", "DOM11", "DOM16", "DOM22", "DOM44", "DOM88", "DOMINOEX", "DOMINOF",
            "VARA HF", "VARA SATELLITE", "VARA FM 1200", "VARA FM 9600",
            "FMHELL", "FSKHELL", "HELL80", "HELLX5", "HELLX9", "HFSK", "PSKHELL", "SLOWHELL",
            "ISCAT-A", "ISCAT-B",
            "JT4A", "JT4B", "JT4C", "JT4D", "JT4E", "JT4F", "JT4G",
            "JT9-1", "JT9-2", "JT9-5", "JT9-10", "JT9-30", "JT9A", "JT9B", "JT9C", "JT9D", "JT9E",
            "JT9E FAST", "JT9F", "JT9F FAST", "JT9G", "JT9G FAST", "JT9H", "JT9H FAST",
            "JT65A", "JT65B", "JT65B2", "JT65C", "JT65C2",
            "FSQCALL", "FST4", "FST4W", "FT4", "JS8", "JTMS",
            "MFSK4", "MFSK8", "MFSK11", "MFSK16", "MFSK22", "MFSK31", "MFSK32", "MFSK64", "MFSK64L",
            "MFSK128", "MFSK128L", "Q65",
            "OLIVIA 4/125", "OLIVIA 4/250", "OLIVIA 8/250", "OLIVIA 8/500",
            "OLIVIA 16/500", "OLIVIA 16/1000", "OLIVIA 32/1000",
            "OPERA-BEACON", "OPERA-QSO",
            "PAC2", "PAC3", "PAC4",
            "PAX2",
            "FSK31", "PSK10", "PSK31", "PSK63", "PSK63F", "PSK63RC4", "PSK63RC5", "PSK63RC10", "PSK63RC20",
            "PSK63RC32", "PSK125", "PSK125C12", "PSK125R", "PSK125RC10", "PSK125RC12", "PSK125RC16",
            "PSK125RC4", "PSK125RC5", "PSK250", "PSK250C6", "PSK250R", "PSK250RC2", "PSK250RC3",
            "PSK250RC5", "PSK250RC6", "PSK250RC7", "PSK500", "PSK500C2", "PSK500C4", "PSK500R",
            "PSK500RC2", "PSK500RC3", "PSK500RC4", "PSK800C2", "PSK800RC2", "PSK1000", "PSK1000C2",
            "PSK1000R", "PSK1000RC2", "PSKAM10", "PSKAM31", "PSKAM50", "PSKFEC31",
            "QPSK31", "QPSK63", "QPSK125", "QPSK250", "QPSK500", "SIM31",
            "QRA64A", "QRA64B", "QRA64C", "QRA64D", "QRA64E",
            "ROS-EME", "ROS-HF", "ROS-MF",
            "LSB", "USB",
            "THOR-M", "THOR4", "THOR5", "THOR8", "THOR11", "THOR16", "THOR22", "THOR25X4",
            "THOR50X1", "THOR50X2", "THOR100",
            "THRBX", "THRBX1", "THRBX2", "THRBX4", "THROB1", "THROB2", "THROB4"
        };

        // The value both "any" entries carry, so an unset dropdown reads as no filter at all.
        private const string AnyItem = "(any)";

        private static string SelectedFilter(ComboBox box)
        {
            string v = box?.SelectedItem as string;
            return string.IsNullOrEmpty(v) || v == AnyItem ? null : v;
        }

        // Lines the Comment BOX up with the dropdowns above it: left edge under Band's dropdown, right
        // edge under Mode's.
        //
        // Measured rather than written as fixed numbers, because everything to the left of those
        // dropdowns is sized by label TEXT - "Country:", "Holyland Square:" - whose width depends on
        // the theme font. Hard-coded values would line up on one machine and be visibly out on the next.
        //
        // Runs after layout, since nothing has a position before then.
        private void AlignCommentBox()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (CB_Band == null || CB_Submode == null || TB_Comment == null ||
                        CommentGroup == null || FiltersPanel == null) return;
                    if (!CB_Band.IsArrangeValid || !CB_Submode.IsArrangeValid || !TB_Comment.IsArrangeValid) return;

                    double LeftOf(FrameworkElement e) =>
                        e.TransformToAncestor(FiltersPanel).Transform(new Point(0, 0)).X;

                    double bandLeft      = LeftOf(CB_Band);
                    double submodeRight  = LeftOf(CB_Submode) + CB_Submode.ActualWidth;
                    double boxLeft       = LeftOf(TB_Comment);

                    // Shift the whole group right so the BOX (not its label) starts under Band. Only
                    // ever rightwards: if the fields ahead of Comment already reach past Band, pulling
                    // left would drag the box over its neighbour, so the left edge is left as it falls
                    // and only the right edge is made to line up.
                    double indent = bandLeft - boxLeft;
                    if (indent > 0)
                    {
                        CommentGroup.Margin = new Thickness(indent, 0, 0, 0);
                        boxLeft = bandLeft;
                    }

                    // Stretch to Submode's right edge (the last item on row 1). The floor keeps the box
                    // usable if the row is ever so crowded that there is almost nothing left.
                    TB_Comment.Width = Math.Max(60, submodeRight - boxLeft);
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // Room a ComboBox needs beyond its text for the drop-down arrow and the gap before it.
        private const double dropDownArrow = 26;

        // The widest entry currently in a dropdown, so it can be sized to its real contents rather
        // than to a guess about what callsigns might exist.
        private static string LongestItem(ComboBox box)
        {
            string longest = string.Empty;
            if (box?.ItemsSource == null) return longest;
            foreach (var item in box.ItemsSource)
            {
                string text = item as string;
                if (text != null && text.Length > longest.Length) longest = text;
            }
            return longest;
        }

        // Sizes a control to fit one specific sample string in its own font.
        //
        // The sibling below measures repeated 'W', which guarantees nothing clips but is far wider than
        // real content - fine for a free-text box, wasteful for a field that only ever holds a grid
        // square. Here the actual worst-case value is measured instead.
        private static void SizeToSample(Control control, string sample, double extra = 0)
        {
            if (control == null || string.IsNullOrEmpty(sample)) return;
            try
            {
                var typeface = new Typeface(control.FontFamily, control.FontStyle, control.FontWeight, control.FontStretch);
                var text = new FormattedText(
                    sample,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    control.FontSize,
                    Brushes.Black,
                    VisualTreeHelper.GetDpi(control).PixelsPerDip);

                control.Width = Math.Ceiling(text.Width)
                              + control.Padding.Left + control.Padding.Right
                              + control.BorderThickness.Left + control.BorderThickness.Right
                              + 10          // caret / breathing room
                              + extra;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }   // keep the XAML fallback width
        }

        // Makes a (non-editable) ComboBox exactly wide enough for its longest item, plus just the toggle
        // arrow. SizeToSample above adds a fixed +10 "caret room" that only matters for an EDITABLE box
        // the operator types into (a plain filter dropdown never gets a caret), so reusing it here left
        // Band / Mode looking barely narrower than their old fixed width. This drops that +10 and uses a
        // tighter arrow allowance, so a short code like "20M" gets a genuinely tight box.
        //
        // maxWidth caps the result. Without it, CB_Submode sized itself to its single longest item - a
        // rare official ADIF entry like "OLIVIA 16/1000" or "VARA SATELLITE" (14 characters) - which alone
        // pushed the whole row past the window's fixed width. The closed box only needs to comfortably
        // show the short, common values (FT8, PSK31, ...); an outlier still selects and searches fine, it
        // just displays clipped in the closed box - the full text is still readable in the open list.
        private static void SizeComboToSample(ComboBox box, string sample, double arrowWidth = 22, double maxWidth = double.PositiveInfinity)
        {
            if (box == null || string.IsNullOrEmpty(sample)) return;
            try
            {
                var typeface = new Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch);
                var text = new FormattedText(
                    sample,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    box.FontSize,
                    Brushes.Black,
                    VisualTreeHelper.GetDpi(box).PixelsPerDip);

                double w = Math.Ceiling(text.Width)
                         + box.Padding.Left + box.Padding.Right
                         + box.BorderThickness.Left + box.BorderThickness.Right
                         + arrowWidth;
                box.Width = Math.Min(w, maxWidth);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }   // keep the XAML fallback width
        }

        // Makes a text box exactly wide enough for `characters` characters of its own font, plus its
        // padding and border.
        //
        // Measured against a realistic callsign-half sample (digits and letters, the actual alphabet a
        // callsign is drawn from) rather than 'W' repeated - 'W' is the single widest capital letter in
        // most UI fonts, so sizing to N of them made the box look far wider than any real callsign half
        // ever fills. A small safety margin (10%) covers the rare word that leans harder on wide letters
        // than this sample does. A fixed pixel width would drift the moment the theme's font or size
        // changed; this asks the font itself.
        private static void SizeToCharacters(TextBox box, int characters)
        {
            try
            {
                var typeface = new Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch);
                string alphabet = "4X0OK1SL2MZ3PQ5"; // digits + letters actually seen in callsigns
                var sample = new char[characters];
                for (int i = 0; i < characters; i++) sample[i] = alphabet[i % alphabet.Length];

                var text = new FormattedText(
                    new string(sample),
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    box.FontSize,
                    Brushes.Black,
                    VisualTreeHelper.GetDpi(box).PixelsPerDip);

                box.Width = Math.Ceiling(text.Width * 1.1)
                          + box.Padding.Left + box.Padding.Right
                          + box.BorderThickness.Left + box.BorderThickness.Right
                          + 6;   // caret room, so the last character is not flush against the border
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }   // keep the XAML fallback width
        }

        // True when a QSO's callsign answers what was typed into the Prefix and Suffix boxes.
        //
        // WHERE a callsign divides is the operator's idea, not the program's. 4Z4DX is "4Z" + "4DX" to
        // anyone reading 4Z as the country, and "4Z4" + "DX" to anyone reading 4Z4 as the call area -
        // both are ordinary ways to look for that station, and both have to find it. The program used to
        // impose its own cut (at the last digit) and compare each box against its own half, so one of the
        // two readings simply found nothing.
        //
        // So no cut point is chosen at all: the callsign must START with what is in Prefix and END with
        // what is in Suffix, without the two overlapping. That holds wherever the operator imagines the
        // division, and it cannot find a station the two fragments do not really belong to.
        //
        // Tested against the identity base form ("4Z5SL/M" -> "4Z5SL") so a portable operation is still
        // found by its suffix, and against the part after a leading stroke so 4X/OK1DL answers to OK1
        // as well as to 4X.
        private static bool CallsignMatchesHalves(string dxCall, string prefix, string suffix)
        {
            if (string.IsNullOrEmpty(dxCall)) return false;

            prefix = prefix ?? string.Empty;
            suffix = suffix ?? string.Empty;

            // The callsign exactly as logged, so a whole call typed into one box finds itself even when
            // it carries a stroke - "4Z5SL/M" is not its own base form, which is "4Z5SL".
            if (WrapsAround(dxCall.Trim(), prefix, suffix)) return true;

            string baseCall = CallsignIdentity.Base(dxCall);
            if (WrapsAround(baseCall, prefix, suffix)) return true;

            int slash = baseCall.LastIndexOf('/');
            if (slash >= 0 && slash < baseCall.Length - 1
                && WrapsAround(baseCall.Substring(slash + 1), prefix, suffix))
                return true;

            // Second chance: the halves as the program splits them. This is what lets a partly typed
            // suffix narrow the list as you type - "D" then "DX" - which ends-with alone cannot do,
            // since a callsign ending in DX does not end in D.
            CallsignIdentity.Split(dxCall, out string qPrefix, out string qSuffix);
            return HalfMatches(qPrefix, prefix, isPrefix: true)
                && HalfMatches(qSuffix, suffix, isPrefix: false);
        }

        // The typed fragments sit at the two ends of this callsign, and do not overlap in the middle.
        // The length test is what stops "4Z4" + "4DX" from claiming 4Z4DX, where the same "4" would
        // have to serve as the end of the prefix and the start of the suffix at once.
        private static bool WrapsAround(string call, string prefix, string suffix)
        {
            if (string.IsNullOrEmpty(call)) return false;
            if (prefix.Length + suffix.Length > call.Length) return false;

            return call.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && call.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        // True when the typed text matches this half of a callsign.
        //
        // "Starts with" rather than "contains", so typing 4Z finds 4Z5SL (prefix 4Z5) without also
        // dragging in every callsign that merely has those letters somewhere. For a prefix carrying a
        // leading stroke the part AFTER the stroke is tried too: 4X/OK1 is found by 4X and by OK1,
        // which are both reasonable things to be looking for.
        private static bool HalfMatches(string half, string typed, bool isPrefix)
        {
            if (string.IsNullOrEmpty(typed)) return true;
            if (string.IsNullOrEmpty(half)) return false;

            if (half.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) return true;

            if (isPrefix)
            {
                int slash = half.LastIndexOf('/');
                if (slash >= 0 && slash < half.Length - 1 &&
                    half.Substring(slash + 1).StartsWith(typed, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private void RunSearch()
        {
            string prefix   = TB_Prefix.Text.Trim();
            string suffix   = TB_Suffix.Text.Trim();
            string country  = CB_Country.Text.Trim();
            string band     = SelectedFilter(CB_Band);
            string mode     = SelectedFilter(CB_Mode);
            string myCall   = SelectedFilter(CB_MyCall);
            string locator  = TB_Locator.Text.Trim();
            string square   = SelectedFilter(CB_Square);
            string comment  = TB_Comment.Text.Trim();
            string lotw     = SelectedFilter(CB_Lotw);
            DateTime? from  = DP_From.SelectedDate;
            DateTime? to    = DP_To.SelectedDate;
            // The rest of the QSO's fields, so every field that defines a QSO is searchable.
            string name      = TB_Name.Text.Trim();
            string oper      = TB_Operator.Text.Trim();
            string freq      = TB_Freq.Text.Trim();
            string submode   = SelectedFilter(CB_Submode);
            string myGrid    = TB_MyGrid.Text.Trim();
            string mySquare  = TB_MySquare.Text.Trim();
            string cqz       = SelectedFilter(CB_CqZone);
            string ituz      = SelectedFilter(CB_ItuZone);
            string continent = SelectedFilter(CB_Continent);
            string state     = SelectedFilter(CB_State);
            string propMode  = TB_PropMode.Text.Trim();
            string satName   = TB_SatName.Text.Trim();
            string soapbox   = TB_Soapbox.Text.Trim();
            string time      = TB_Time.Text.Trim();
            string qth       = TB_Qth.Text.Trim();
            string qrz       = SelectedFilter(CB_Qrz);
            string eqsl      = SelectedFilter(CB_Eqsl);
            string clublog   = SelectedFilter(CB_Clublog);
            string paper     = SelectedFilter(CB_Paper);
            string review    = SelectedFilter(CB_Review);

            // No filter set means show the WHOLE log, the way a spreadsheet shows every row until you
            // filter it. An empty grid told the operator nothing about what was in the log and made the
            // window feel broken before the first search.
            bool unfiltered = string.IsNullOrEmpty(prefix) && string.IsNullOrEmpty(suffix) &&
                              string.IsNullOrEmpty(country) && band == null && mode == null &&
                              myCall == null && string.IsNullOrEmpty(locator) &&
                              string.IsNullOrEmpty(square) && string.IsNullOrEmpty(comment) &&
                              lotw == null && from == null && to == null &&
                              string.IsNullOrEmpty(name) && string.IsNullOrEmpty(oper) &&
                              string.IsNullOrEmpty(freq) && submode == null &&
                              string.IsNullOrEmpty(myGrid) && string.IsNullOrEmpty(mySquare) &&
                              string.IsNullOrEmpty(cqz) && string.IsNullOrEmpty(ituz) &&
                              continent == null && string.IsNullOrEmpty(propMode) &&
                              string.IsNullOrEmpty(satName) && string.IsNullOrEmpty(soapbox) &&
                              string.IsNullOrEmpty(time) && string.IsNullOrEmpty(state) &&
                              string.IsNullOrEmpty(qth) &&
                              qrz == null && eqsl == null && clublog == null && paper == null &&
                              review == null;

            var results = _allQsos.AsEnumerable();

            if (!string.IsNullOrEmpty(prefix) || !string.IsNullOrEmpty(suffix))
                results = results.Where(q => CallsignMatchesHalves(q.DXCall, prefix, suffix));

            // Country is enforced ONLY when no prefix is given. When a prefix IS given, the Country box was
            // auto-filled from it for display, but the real criterion is the callsign prefix - so a QSO
            // with that prefix but a blank/differing Country field is still found.
            if (!string.IsNullOrEmpty(country) && string.IsNullOrEmpty(prefix))
            {
                // An entity NUMBER is matched as a number - against the countries in this log that
                // carry it - never as text inside a country name. Typing 5 must mean entity 5, not
                // every QSO whose country happens to contain the character 5. A picked country
                // arrives here as "336  Israel" and is matched by its number alone, so the name half
                // may be clipped in the box without changing what the search does.
                int wantedCode; string wantedName;
                ReadCountryText(country, out wantedCode, out wantedName);

                if (wantedCode > 0)
                {
                    var named = new HashSet<string>(
                        _allCountries.Where(c => c.Code == wantedCode).Select(c => c.Name),
                        StringComparer.OrdinalIgnoreCase);
                    results = results.Where(q => q.Country != null && named.Contains(q.Country.Trim()));
                }
                else
                {
                    results = results.Where(q => q.Country != null &&
                        q.Country.IndexOf(wantedName, StringComparison.OrdinalIgnoreCase) >= 0);
                }
            }

            // Band / mode / my callsign come from dropdowns built out of the log itself, so they are
            // exact matches - picking "20M" must not also bring in "20M" QSOs of some other band whose
            // name merely contains it.
            //
            // Band is enforced ONLY when no frequency is given. When a frequency IS given, the Band box was
            // auto-filled from it for display, but the real criterion is the frequency - so we match on
            // frequency alone and ignore the band, otherwise a QSO logged with a frequency but a blank Band
            // field would be wrongly excluded.
            if (band != null && string.IsNullOrEmpty(freq))
                results = results.Where(q => string.Equals(q.Band, band, StringComparison.OrdinalIgnoreCase));

            if (mode != null)
                results = results.Where(q => string.Equals(q.Mode, mode, StringComparison.OrdinalIgnoreCase));

            if (myCall != null)
                results = results.Where(q => string.Equals(q.MyCall, myCall, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(locator))
                results = results.Where(q => q.DXLocator != null &&
                    q.DXLocator.IndexOf(locator, StringComparison.OrdinalIgnoreCase) >= 0);

            // Holyland square, CQ / ITU zone and State are picked from the log's own values now, so they
            // are exact matches like Band and Mode. As free text they were "contains" matches, where
            // zone 1 also returned 10, 11, 12 ... 19 and every other zone with a 1 in it.
            if (square != null)
                results = results.Where(q => string.Equals(q.SRX, square, StringComparison.OrdinalIgnoreCase));

            if (lotw != null)
            {
                bool wantConfirmed = lotw == LotwConfirmed;
                results = results.Where(q => (q.LotwQslRcvd == 1) == wantConfirmed);
            }

            // The rest of the QSO's fields - all "contains" matches, like Locator / Square / Comment.
            if (!string.IsNullOrEmpty(name))
                results = results.Where(q => q.Name != null && q.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
            if (!string.IsNullOrEmpty(oper))
                results = results.Where(q => q.Operator != null && q.Operator.IndexOf(oper, StringComparison.OrdinalIgnoreCase) >= 0);
            if (!string.IsNullOrEmpty(freq))
                results = results.Where(q => q.Freq != null && q.Freq.IndexOf(freq, StringComparison.OrdinalIgnoreCase) >= 0);
            if (submode != null)
                results = results.Where(q => string.Equals(q.SUBMode, submode, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(myGrid))
                results = results.Where(q => q.MyLocator != null && q.MyLocator.IndexOf(myGrid, StringComparison.OrdinalIgnoreCase) >= 0);
            if (!string.IsNullOrEmpty(mySquare))
                results = results.Where(q => q.STX != null && q.STX.IndexOf(mySquare, StringComparison.OrdinalIgnoreCase) >= 0);
            if (cqz != null)
                results = results.Where(q => string.Equals(q.CQZone, cqz, StringComparison.OrdinalIgnoreCase));
            if (ituz != null)
                results = results.Where(q => string.Equals(q.ITUZone, ituz, StringComparison.OrdinalIgnoreCase));
            // Continent is enforced ONLY when no prefix is given - with a prefix the continent is implied by
            // the callsign (and shown locked), so the prefix is the real criterion.
            if (continent != null && string.IsNullOrEmpty(prefix))
                results = results.Where(q => string.Equals(q.Continent, continent, StringComparison.OrdinalIgnoreCase));
            if (state != null)
                results = results.Where(q => string.Equals(q.State, state, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(propMode))
                results = results.Where(q => q.PROP_MODE != null && q.PROP_MODE.IndexOf(propMode, StringComparison.OrdinalIgnoreCase) >= 0);
            if (!string.IsNullOrEmpty(satName))
                results = results.Where(q => q.SAT_NAME != null && q.SAT_NAME.IndexOf(satName, StringComparison.OrdinalIgnoreCase) >= 0);
            if (!string.IsNullOrEmpty(soapbox))
                results = results.Where(q => q.SOAPBOX != null && q.SOAPBOX.IndexOf(soapbox, StringComparison.OrdinalIgnoreCase) >= 0);
            if (!string.IsNullOrEmpty(time))
                results = results.Where(q => q.Time != null && q.Time.IndexOf(time, StringComparison.OrdinalIgnoreCase) >= 0);
            // QTH is free text rather than a dropdown of the log's own values: a log can hold thousands of
            // distinct towns, which is not a list anyone picks from. "Contains", so haifa finds Haifa Bay.
            if (!string.IsNullOrEmpty(qth))
                results = results.Where(q => q.Qth != null && q.Qth.IndexOf(qth, StringComparison.OrdinalIgnoreCase) >= 0);

            // The other confirmation sources, same "confirmed / not confirmed" logic as LoTW.
            if (qrz != null)     { bool w = qrz == LotwConfirmed;     results = results.Where(q => (q.QrzQslRcvd == 1) == w); }
            if (eqsl != null)    { bool w = eqsl == LotwConfirmed;    results = results.Where(q => (q.EqslQslRcvd == 1) == w); }
            if (clublog != null) { bool w = clublog == LotwConfirmed; results = results.Where(q => (q.ClublogQslRcvd == 1) == w); }
            if (paper != null)   { bool w = paper == LotwConfirmed;   results = results.Where(q => (q.PaperQslRcvd == 1) == w); }

            // Whether the Log Fixer has actually written to this contact. Yes is the log as the Fixer
            // left it; No is everything it has not corrected - the ones never checked and the ones the
            // operator looked at and decided were right already.
            if (review != null)
            {
                bool wantFixed = review == FixedYes;
                results = results.Where(q => (q.ReviewState == 1) == wantFixed);
            }

            // Comments are free text, so this is a "contains" match - the useful thing is finding the
            // QSO where you noted something, not matching how the note began.
            if (!string.IsNullOrEmpty(comment))
                results = results.Where(q => q.Comment != null &&
                    q.Comment.IndexOf(comment, StringComparison.OrdinalIgnoreCase) >= 0);

            // Dates are held as text; compare on the yyyyMMdd form so the ordering is the string
            // ordering and no per-QSO DateTime parsing is needed. "To" is inclusive of that whole day.
            if (from != null)
            {
                string fromKey = from.Value.ToString("yyyyMMdd");
                results = results.Where(q => string.Compare(DateKey(q.Date), fromKey, StringComparison.Ordinal) >= 0);
            }
            if (to != null)
            {
                string toKey = to.Value.ToString("yyyyMMdd");
                results = results.Where(q => string.Compare(DateKey(q.Date), toKey, StringComparison.Ordinal) <= 0);
            }

            // Newest first, sorted explicitly rather than relying on the order the log happened to hand
            // us - the Date header carries a sort arrow from the moment results appear, and an arrow
            // that is not backed by a real sort is worse than no arrow at all.
            var found = new ObservableCollection<QSO>(
                results.OrderByDescending(q => DateKey(q.Date), StringComparer.Ordinal)
                       .ThenByDescending(q => TimeKey(q.Time), StringComparer.Ordinal));
            // AFTER the rows are bound, not before. Clearing first cleared the ticks but left the
            // DataGrid's own selection holding the very same QSO objects - the search re-runs over the
            // same log - so the highlight survived and Esc emptied every box while the rows stayed lit
            // up. Clearing afterwards clears both, which is now one thing anyway.
            ResultsGrid.DataContext = found;
            ClearPicks();                    // these are different rows now - see ClearPicks
            ShowDateSortIndicator();
            UpdatePickState();
            TB_Count.Text = found.Count == 1 ? "1 QSO" : $"{found.Count:N0} QSOs";
            TB_Status.Text = unfiltered
                ? $"Showing the whole log — {found.Count:N0} QSO{(found.Count == 1 ? "" : "s")}. Set any filter above to narrow it down."
                : found.Count == 0
                    ? "No QSOs found."
                    : $"{found.Count:N0} QSO{(found.Count == 1 ? "" : "s")} found.";
        }

        // A QSO date reduced to yyyyMMdd for comparison, whatever separators it was stored with
        // ("2019-03-14", "20190314" and "2019/03/14" all become "20190314").
        private static string DateKey(string date)
        {
            if (string.IsNullOrWhiteSpace(date)) return string.Empty;
            var sb = new System.Text.StringBuilder(8);
            foreach (char c in date)
            {
                if (char.IsDigit(c)) sb.Append(c);
                if (sb.Length == 8) break;
            }
            return sb.ToString();
        }

        // A QSO time reduced to digits ("18:43" -> "1843") so it compares as text in clock order.
        private static string TimeKey(string time)
        {
            if (string.IsNullOrWhiteSpace(time)) return string.Empty;
            var sb = new System.Text.StringBuilder(6);
            foreach (char c in time)
            {
                if (char.IsDigit(c)) sb.Append(c);
                if (sb.Length == 6) break;
            }
            return sb.ToString();
        }

        // Puts the sort arrow on Date (descending), matching how the results were just ordered, and
        // takes it off every other column so only one arrow is ever showing.
        private void ShowDateSortIndicator()
        {
            foreach (var column in ResultsGrid.Columns)
                column.SortDirection = null;
            if (Col_Date != null)
                Col_Date.SortDirection = ListSortDirection.Descending;
        }

        // Fills every "pick one" filter from what the log actually contains, so the lists can only
        // offer choices that can return something. A dropdown of the log's own values also answers a
        // question a text box could not: what IS in this log - which zones, which states, which
        // squares - without searching for each one to find out.
        //
        // Called again whenever the window is pointed at a different collection (ReplaceSource), because
        // the lists describe THAT log: an Exchange list left over from the previous log would offer
        // values this one never received, and hide the ones it did.
        private void PopulateFilterLists()
        {
            // Puts a list into a box while keeping whatever was selected, if the new list still has it.
            // Re-populating must not silently drop a filter the operator set - the grid would widen
            // underneath them with no visible reason.
            void SetItems(ComboBox box, List<string> values)
            {
                string keep = box.SelectedItem as string;
                box.ItemsSource = values;
                int at = keep == null ? 0 : values.FindIndex(v => string.Equals(v, keep, StringComparison.OrdinalIgnoreCase));
                box.SelectedIndex = at >= 0 ? at : 0;
            }

            // numeric: sort as numbers, not text. The zones are held as text, so plain string ordering
            // put 10 before 9 (and 1, 10, 11, ... 2). Any value that isn't a number sorts after the
            // numbers, alphabetically, rather than being dropped.
            void Fill(ComboBox box, System.Func<QSO, string> pick, bool numeric = false, bool disableWhenEmpty = false)
            {
                var values = _allQsos
                    .Select(pick)
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (numeric)
                    values = values
                        .OrderBy(v => { int n; return int.TryParse(v, out n) ? 0 : 1; })
                        .ThenBy(v => { int n; return int.TryParse(v, out n) ? n : 0; })
                        .ThenBy(v => v, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                else
                    values = values.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();

                // Greyed out when the log holds no value at all for that field - so an empty State or
                // Exchange list reads as "this log has none", not as a filter that failed to load.
                if (disableWhenEmpty) box.IsEnabled = values.Count > 0;

                values.Insert(0, AnyItem);
                SetItems(box, values);
            }

            Fill(CB_Band, q => q.Band);
            Fill(CB_Mode, q => q.Mode);
            Fill(CB_MyCall, q => q.MyCall);
            Fill(CB_Continent, q => q.Continent);

            // Band and Continent are left out of disableWhenEmpty on purpose: both are also switched on
            // and off by the Frequency / Prefix boxes, and a second rule touching IsEnabled would fight it.
            Fill(CB_Submode, q => q.SUBMode, disableWhenEmpty: true);
            Fill(CB_CqZone,  q => q.CQZone,  numeric: true, disableWhenEmpty: true);
            Fill(CB_ItuZone, q => q.ITUZone, numeric: true, disableWhenEmpty: true);
            Fill(CB_State,   q => q.State,   disableWhenEmpty: true);
            Fill(CB_Square,  q => q.SRX,     disableWhenEmpty: true);

            // Fixed choices, not values found in the log: "not confirmed" has to be offerable even when
            // every QSO happens to be confirmed, and the other way round.
            foreach (var cb in new[] { CB_Lotw, CB_Qrz, CB_Eqsl, CB_Clublog, CB_Paper })
                SetItems(cb, new List<string> { AnyItem, LotwConfirmed, LotwNotConfirmed });

            // Fixed choices too, and only two of them: has the Log Fixer written to this QSO or not.
            // The database keeps three states - never reviewed, corrected, reviewed and left alone -
            // but the question this box answers is the plain one, so "No" is everything that was not
            // corrected, whether it was ever looked at or not.
            SetItems(CB_Review, new List<string> { AnyItem, FixedYes, FixedNo });
        }

        private const string LotwConfirmed = "Confirmed";
        private const string LotwNotConfirmed = "Not confirmed";

        // Was this QSO put right by the Log Fixer? Yes is review_state 1; No is everything else.
        private const string FixedYes = "Yes";
        private const string FixedNo = "No";

        // Dropdown / date-picker changes only refresh the Clear button. The search still runs on
        // Search, Enter or Esc, so picking a band does not fire a search through a half-typed callsign.
        private void Filter_Changed(object sender, RoutedEventArgs e) => UpdateClearButton();

        // ---- Undo -----------------------------------------------------------------------------------
        //
        // Every committed cell edit becomes one step, kept while the window is open. A step holds ALL
        // the fields that actually changed, not just the cell that was typed in: editing the frequency
        // also rewrites the band, and undoing one without the other would leave a QSO with the old
        // frequency and the new band - a contradiction neither value would explain.
        //
        // The changed set is found by comparing a snapshot taken before the save with the values after
        // it, so a derived rewrite is captured automatically rather than by remembering to list it here.
        private class EditStep
        {
            public QSO Qso;
            public Dictionary<string, string> Before;   // only the fields that changed (null for a delete)
            public string Label;
            public bool IsDelete;                        // true = this step deleted Qso; undo re-inserts it
            public long LogId;                           // the log to restore a deleted QSO into

            // A multi-row delete from the selection menu: ONE step that removed all of these, so one
            // press of Undo puts all of them back. Null for every other kind of step.
            public List<QSO> BatchQsos;
            public List<long> BatchLogIds;               // the log each of them came from, same order
        }

        private readonly Stack<EditStep> _undo = new Stack<EditStep>();

        private static readonly string[] EditableFields =
            { "Date", "Time", "Name", "Freq", "Band", "RST_RCVD", "RST_SENT", "Mode", "SRX", "Comment" };

        private static readonly Dictionary<string, System.Reflection.PropertyInfo> FieldProps = BuildFieldProps();

        private static Dictionary<string, System.Reflection.PropertyInfo> BuildFieldProps()
        {
            var map = new Dictionary<string, System.Reflection.PropertyInfo>(StringComparer.Ordinal);
            foreach (string field in EditableFields)
            {
                var property = typeof(QSO).GetProperty(field);
                if (property != null && property.CanRead && property.CanWrite && property.PropertyType == typeof(string))
                    map[field] = property;
            }
            return map;
        }

        private static Dictionary<string, string> Snapshot(QSO qso)
        {
            var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in FieldProps)
                snapshot[pair.Key] = pair.Value.GetValue(qso) as string;
            return snapshot;
        }

        // Column headers, so the status line says "RST-R" rather than "RST_RCVD".
        private static string Pretty(string field)
        {
            switch (field)
            {
                case "RST_RCVD": return "RST-R";
                case "RST_SENT": return "RST-S";
                case "Freq":     return "Frequency";
                case "SRX":      return "Exchange";
                default:         return field;
            }
        }

        // The fields the editor actually altered, each mapped to the value it held before. Empty means
        // the commit changed nothing at all - which is the normal case, because the DataGrid opens a
        // cell's editor on a plain click on the cell that is already current. See
        // ResultsGrid_CellEditEnding.
        private static Dictionary<string, string> ChangedFields(QSO qso, Dictionary<string, string> before)
        {
            var changed = new Dictionary<string, string>(StringComparer.Ordinal);
            var after = Snapshot(qso);
            foreach (var pair in before)
            {
                string was = pair.Value ?? string.Empty;
                after.TryGetValue(pair.Key, out string now);
                if (!string.Equals(was, now ?? string.Empty, StringComparison.Ordinal))
                    changed[pair.Key] = pair.Value;
            }
            return changed;
        }

        private void RecordUndoStep(QSO qso, Dictionary<string, string> before)
        {
            var changed = ChangedFields(qso, before);
            if (changed.Count == 0) return;   // committed the same value: nothing to undo

            var names = new List<string>();
            foreach (string key in changed.Keys) names.Add(Pretty(key));

            _undo.Push(new EditStep
            {
                Qso = qso,
                Before = changed,
                Label = $"{qso.DXCall} — {string.Join(", ", names)}"
            });
            UpdateUndoButton();
        }

        private void UpdateUndoButton()
        {
            if (Btn_Undo == null) return;
            bool any = _undo.Count > 0;
            Btn_Undo.IsEnabled = any;

            // THE ARROW IS PART OF THE BUTTON, not decoration on the XAML. This line used to write a
            // bare "Undo" over it, so the button lost its arrow the moment anything was undone and
            // looked like a different button from the one that had been there a second earlier.
            Btn_Undo.Content = any ? string.Format("↶ Undo ({0})", _undo.Count) : "↶ Undo";

            // THE COLOURS BELONG TO THE STYLE. They used to be painted on here - red while there was
            // something to undo, cleared when there was not - and a local value outranks a style, so
            // the red keycap could not colour its own face, and clearing it left a plain button beside
            // a styled one. DangerActionButtonStyle already says red at rest, blue under the mouse and
            // grey when disabled; IsEnabled above is all it needs to be told.
            Btn_Undo.ToolTip = any
                ? "Undo: " + _undo.Peek().Label
                : "Nothing to undo. Changes made in this window can be undone until it is closed.";
        }

        private void Btn_Undo_Click(object sender, RoutedEventArgs e) => UndoLastEdit();

        // Re-reads the rows so a changed display option (the LoTW callsign tint) shows immediately.
        internal void RefreshRows()
        {
            if (_cellInEdit) return;   // a refresh inside an edit transaction is not allowed
            try { ResultsGrid.Items.Refresh(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Points the window at a freshly-loaded QSO collection and re-runs the current search, so an
        // OPEN Search window reflects data that changed underneath it - the LoTW confirmation marking
        // reassigns the main window's collection, and without this the search would keep showing the
        // old objects, which never received the ticks.
        // What the caption calls the set of QSOs on show. Normally the log's name; a window opened on a
        // SLICE of a log (the Statistics window's deleted entities, say) says which slice, so a count far
        // smaller than the log's does not read as data gone missing.
        // VERIFY, over exactly the rows on screen. The import report ends by saying Verify is where the
        // findings can be acted on, and this is that button: it checks what the filters above have
        // narrowed the log down to, so an operator can work through one country, one year or one band
        // at a time instead of the whole logbook at once.
        //
        // Nothing is written by opening it. The verifier lists what it finds, ticks nothing, copies the
        // database before it writes, and only touches the QSOs whose rows were ticked.
        private void Btn_Verify_Click(object sender, RoutedEventArgs e)
        {
            var shown = ResultsGrid.DataContext as System.Collections.Generic.IEnumerable<QSO>;
            var list = shown == null ? new System.Collections.Generic.List<QSO>() : shown.ToList();
            if (list.Count == 0)
            {
                HolyMessageBox.ShowWarning("There are no QSOs on screen to check.", "Log Fixer", this);
                return;
            }

            try
            {
                // Named with the count, because this is usually NOT the whole log and the window must
                // not look as though it has checked one when it has checked twenty-six.
                string what = TB_TitleLog != null && !string.IsNullOrWhiteSpace(TB_TitleLog.Text)
                    ? TB_TitleLog.Text.Trim()
                    : "this log";
                var verifier = new LogVerifierWindow(list, $"{what}  ({list.Count:N0} QSOs)") { Owner = this };
                verifier.ShowDialog();
                ResultsGrid.Items.Refresh();   // corrections are written into the very objects shown here
            }
            catch (Exception ex)
            {
                HolyMessageBox.ShowError("Could not check these QSOs.\n\n" + ex.Message, "Log Fixer", this);
            }
        }

        public void SetTitleLog(string label)
        {
            string text = string.IsNullOrWhiteSpace(label) ? "(unnamed log)" : label.Trim();
            TB_TitleLog.Text = text;
            Title = "Log Workshop — " + text;
        }

        public void ReplaceSource(ObservableCollection<QSO> qsos)
        {
            if (qsos == null || _cellInEdit) return;
            if (_allQsos != null) _allQsos.CollectionChanged -= OnSourceCollectionChanged;
            _allQsos = qsos;
            try
            {
                // The dropdowns are built FROM the log, so a new collection means new lists. Selections
                // that still exist in the new log survive; ones that don't fall back to "(any)".
                PopulateFilterLists();
                SizeFilterListsToContent();
                WatchSourceForNewValues();
                RunSearch();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // A QSO logged while this window is open can carry a value no other QSO has - the first QSO
        // into a state, a zone or a Holyland square. Without this the dropdowns would keep describing
        // the log as it was when the window opened, and that square could not be picked until it was
        // closed and reopened.
        private void WatchSourceForNewValues()
        {
            if (_allQsos == null) return;
            _allQsos.CollectionChanged += OnSourceCollectionChanged;
        }

        private void OnSourceCollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            // A CONTACT DELETED ANYWHERE IS GONE FROM HERE TOO.
            //
            // This window used to keep showing a QSO after it had been deleted in the log table - and
            // its rows are not a picture, they are the thing itself: that row could still be opened,
            // edited, uploaded or exported, all of it aimed at a contact the database no longer holds.
            //
            // Done BEFORE the two guards below, because they are about not disturbing a dropdown or a
            // cell mid-edit, and a row that should not exist outranks both.
            //
            // Only removals are acted on. A newly logged QSO does NOT re-run the search: that would
            // throw away the operator's ticks and his place in the list every time he logs a contact,
            // and a result set missing a new row is merely incomplete, where a deleted row is wrong.
            if (e.OldItems != null && e.OldItems.Count > 0)
            {
                try
                {
                    var shown = ResultsGrid?.DataContext as ObservableCollection<QSO>;
                    if (shown != null)
                        foreach (QSO gone in e.OldItems.OfType<QSO>())
                            shown.Remove(gone);
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }

            // Not while a cell is being edited, and not while a list is open under the operator's
            // cursor - replacing the items of an open dropdown closes it mid-choice.
            if (_cellInEdit) return;
            foreach (var cb in new[] { CB_Band, CB_Mode, CB_MyCall, CB_Continent, CB_Submode,
                                       CB_CqZone, CB_ItuZone, CB_State, CB_Square })
                if (cb != null && cb.IsDropDownOpen) return;

            try
            {
                PopulateFilterLists();
                SizeFilterListsToContent();
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        private void UndoLastEdit()
        {
            if (_undo.Count == 0) return;

            EditStep step = _undo.Pop();
            try
            {
                if (step.IsDelete && step.BatchQsos != null)
                {
                    // A selection delete: every QSO goes back into the log it came from, in one go.
                    var dalBatch = DataAccess.GetInstance();
                    int restored = 0;
                    for (int i = 0; i < step.BatchQsos.Count; i++)
                    {
                        QSO q = step.BatchQsos[i];
                        int newId = dalBatch?.RestoreQso(q, step.BatchLogIds[i]) ?? 0;
                        if (newId > 0) q.id = newId;
                        _allQsos?.Add(q);
                        restored++;
                    }
                    try { RunSearch(); } catch (Exception swallowed) { Log.Swallow(swallowed); }

                    TB_Status.Text = $"Restored {restored:N0} QSOs."
                                   + (_undo.Count > 0 ? $"  {_undo.Count} more can be undone." : "  Nothing left to undo.");
                    UpdateUndoButton();
                    return;
                }

                if (step.IsDelete)
                {
                    // Re-insert the deleted QSO into its original log, add it back to the source list, then
                    // rebuild the results so it reappears in the right (sorted) place - a plain Add to the
                    // bound collection can leave it unsorted/off-screen, which looks like "nothing came back".
                    int newId = DataAccess.GetInstance()?.RestoreQso(step.Qso, step.LogId) ?? 0;
                    if (newId > 0) step.Qso.id = newId;
                    _allQsos?.Add(step.Qso);
                    try { RunSearch(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                    try { ResultsGrid.SelectedItem = step.Qso; ResultsGrid.ScrollIntoView(step.Qso); }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }

                    TB_Status.Text = $"Restored {step.Qso.DXCall}."
                                   + (_undo.Count > 0 ? $"  {_undo.Count} more can be undone." : "  Nothing left to undo.");
                    UpdateUndoButton();
                    return;
                }

                foreach (var pair in step.Before)
                    FieldProps[pair.Key].SetValue(step.Qso, pair.Value);

                DataAccess.GetInstance()?.Update(step.Qso);
                try { ResultsGrid.Items.Refresh(); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }

                // Said plainly: putting the values back does NOT chase the QSL services, which may
                // already have been handed the corrected version.
                TB_Status.Text = $"Undone: {step.Label}."
                               + (_undo.Count > 0 ? $"  {_undo.Count} more can be undone." : "  Nothing left to undo.")
                               + "  (Any upload already queued is not withdrawn.)";
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                TB_Status.Text = "Could not undo that change: " + ex.Message;
            }
            UpdateUndoButton();
        }

        // ---- Re-uploading a QSO after it has been corrected -----------------------------------------
        //
        // Held per QSO rather than per cell: correcting Name, then RST, then Comment on one contact is
        // one correction, and asking three times would train the operator to dismiss the question.
        private QSO _editedQso;
        private readonly SortedSet<string> _editedFields = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        // Set when a save rewrote a cell the operator did not type in; acted on once the row's edit
        // transaction has closed (see SaveEditedQso).
        private bool _refreshWhenRowDone;

        // True between opening a cell editor and closing it, so the window-level Esc handler can stand
        // aside and let the DataGrid cancel the edit.
        private bool _cellInEdit;

        private void RememberEdit(QSO qso, string fieldLabel)
        {
            // Moved to a different QSO without a row commit in between - settle up for the old one.
            if (_editedQso != null && !ReferenceEquals(_editedQso, qso))
                OfferReupload();

            _editedQso = qso;
            if (!string.IsNullOrWhiteSpace(fieldLabel)) _editedFields.Add(fieldLabel);
        }

        // The upload services this operator actually uses, each flagged with whether it already holds
        // this QSO.
        private static List<KeyValuePair<string, bool>> ServicesFor(QSO qso)
        {
            var list = new List<KeyValuePair<string, bool>>();
            var s = Properties.Settings.Default;
            if (s.UseLotwService)    list.Add(new KeyValuePair<string, bool>("LoTW", qso.LotwStatus == 1));
            if (s.UseEqslService)    list.Add(new KeyValuePair<string, bool>("eQSL", qso.EqslStatus == 1));
            if (s.UseQrzLogbook)     list.Add(new KeyValuePair<string, bool>("QRZ Logbook", qso.QrzStatus == 1));
            if (s.UseClublogService) list.Add(new KeyValuePair<string, bool>("Club Log", qso.ClublogStatus == 1));
            return list;
        }

        // Offers to send the corrected QSO to the logging services, by putting it back in their normal
        // pending queues - the same queues a brand-new QSO goes through, so no second upload path
        // exists to drift out of step with the first.
        private void OfferReupload()
        {
            QSO qso = _editedQso;
            var fields = new List<string>(_editedFields);
            _editedQso = null;
            _editedFields.Clear();

            if (qso == null || fields.Count == 0) return;

            var services = ServicesFor(qso);
            if (services.Count == 0) return;   // no upload services switched on - nothing to offer

            try
            {
                bool anyAlreadySent = services.Exists(x => x.Value);

                var text = new StringBuilder();
                text.AppendLine($"{qso.DXCall}   {qso.Date} {qso.Time}");
                text.AppendLine("Changed: " + string.Join(", ", fields));
                text.AppendLine();
                text.AppendLine("Send the corrected QSO to:");
                foreach (var service in services)
                    text.AppendLine("    • " + service.Key + (service.Value ? "     (already uploaded)" : ""));

                if (anyAlreadySent)
                {
                    text.AppendLine();
                    text.AppendLine("A service that already holds this QSO usually IGNORES a repeat upload, so the");
                    text.AppendLine("correction may never reach it. And if the date, time, mode or frequency changed,");
                    text.AppendLine("it may store the contact a SECOND time instead of replacing the old one.");
                }

                text.AppendLine();
                text.Append("Put it back in the upload queue?");

                if (!HolyMessageBox.ShowConfirm(text.ToString(), "Upload the corrected QSO?",
                                                HolyMsgType.Warning, this))
                    return;

                var dal = DataAccess.GetInstance();
                if (dal == null) return;

                var s = Properties.Settings.Default;
                if (s.UseLotwService)    { dal.SetLotwStatus(qso.id, 0);    qso.LotwStatus = 0; }
                if (s.UseEqslService)    { dal.SetEqslStatus(qso.id, 0);    qso.EqslStatus = 0; }
                if (s.UseQrzLogbook)     { dal.SetQrzStatus(qso.id, 0);     qso.QrzStatus = 0; }
                if (s.UseClublogService) { dal.SetClublogStatus(qso.id, 0); qso.ClublogStatus = 0; }

                TB_Status.Text = $"{qso.DXCall} queued for upload to " +
                                 string.Join(", ", services.ConvertAll(x => x.Key)) + ".";
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                TB_Status.Text = "Could not queue the QSO for upload: " + ex.Message;
            }
        }

        // Leaving the row means the correction to THIS QSO is finished.
        private void ResultsGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit) return;
            // Deferred for the same reason as the cell commit: the final write lands after this event,
            // and the row's edit transaction is only closed once it has.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_refreshWhenRowDone)
                {
                    _refreshWhenRowDone = false;
                    try { ResultsGrid.Items.Refresh(); }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }
                }
                OfferReupload();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        // Band is not editable while the frequency already decides it.
        //
        // The band is derived from the frequency, so editing it on its own can only ever produce a
        // contradiction - or cost precision, since making the pair agree again would round a logged
        // 14.206000 down to the band's plain 14.2. Change the frequency and the band follows.
        //
        // The exception matters: when the frequency is missing, or is a value no band table
        // recognises, it decides nothing and the band has to stay editable, otherwise exactly the rows
        // that need repairing would be the ones that could not be repaired.
        private void ResultsGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
        {
            if (e.Column?.SortMemberPath != "Band") return;

            var qso = e.Row?.Item as QSO;
            if (qso == null) return;

            string mhz = HolyLogParser.NormalizeFreqToMhz(qso.Freq);
            string derived = mhz == null ? null : HolyLogParser.convertFreqToBand(mhz);
            if (string.IsNullOrWhiteSpace(derived)) return;   // frequency settles nothing - allow it

            e.Cancel = true;
            TB_Status.Text = $"Band is set by the frequency ({qso.Freq}). "
                           + "Edit the frequency and the band follows.";
        }

        // Runs after the handler above, so a cell it refused is not counted as being in edit.
        private void ResultsGrid_PreparingCellForEdit(object sender, DataGridPreparingCellForEditEventArgs e)
            => _cellInEdit = true;

        // Edits are made in the results table itself, spreadsheet style, and saved when the cell is
        // left. Date, Time and Callsign are deliberately NOT editable: the callsign cell is the QRZ
        // link, and a click that sometimes opens a web page and sometimes starts typing over a
        // callsign is the kind of surprise that loses log data.
        private void ResultsGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            _cellInEdit = false;
            RelockAfterEdit();   // a double-click opened this cell; the grid goes back to read-only
            if (e.EditAction != DataGridEditAction.Commit) return;   // Esc: nothing was changed

            var qso = e.Row?.Item as QSO;
            if (qso == null) return;

            string edited = e.Column?.SortMemberPath;
            string label = e.Column?.Header?.ToString();

            // Captured BEFORE the editor writes back. Two jobs: it is what Undo restores, and it guards
            // against a dropdown destroying a value it does not recognise - a QSO on a band outside
            // KnownBands (say 4M) leaves the ComboBox with nothing selected, and committing that would
            // write a null straight over the band.
            Dictionary<string, string> before = Snapshot(qso);

            // The editor writes its value back as part of committing, which finishes AFTER this event.
            // Saving here would store the OLD value, so the write is deferred one turn.
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    // A commit that changed NOTHING is not a correction, and it is the common case: the
                    // DataGrid opens a cell's editor on a plain click on the cell that is already
                    // current, so clicking a row twice - or clicking one row and then another - ran the
                    // whole correction path. That wrote the QSO back to the log and asked "Upload the
                    // corrected QSO?" about a contact nobody had touched.
                    if (ChangedFields(qso, before).Count == 0) return;

                    SaveEditedQso(qso, edited, before);
                    RecordUndoStep(qso, before);
                    RememberEdit(qso, label);
                }),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private void SaveEditedQso(QSO qso, string edited, Dictionary<string, string> before)
        {
            try
            {
                // Never let an unrecognised value be blanked out by the dropdown (see above).
                if (string.IsNullOrWhiteSpace(qso.Band)) qso.Band = before["Band"];
                if (string.IsNullOrWhiteSpace(qso.Mode)) qso.Mode = before["Mode"];

                // Frequency and band are uploaded together and TQSL rejects a contact where they
                // contradict each other, so an edit to either keeps the pair honest.
                string note = string.Empty;
                if (edited == "Freq")
                {
                    // The frequency is the precise value, so it decides the band.
                    string mhz = HolyLogParser.NormalizeFreqToMhz(qso.Freq);
                    string band = mhz == null ? null : HolyLogParser.convertFreqToBand(mhz);
                    if (!string.IsNullOrWhiteSpace(band) &&
                        !string.Equals(band, qso.Band, StringComparison.OrdinalIgnoreCase))
                    {
                        qso.Band = band;
                        note = $"  (band set to {band})";
                    }
                }
                else if (edited == "Band")
                {
                    // Reachable only for a QSO whose frequency decides nothing (see
                    // ResultsGrid_BeginningEdit). A MISSING frequency is filled in from the band, which
                    // at least puts the QSO in the right part of the spectrum for an upload. A
                    // frequency that is present but unrecognised is left strictly alone: it is real
                    // logged data, and replacing it with a round number would destroy the only record
                    // of where the contact actually was.
                    if (string.IsNullOrWhiteSpace(qso.Freq))
                    {
                        string standard = HolyLogParser.convertBandToFreq(qso.Band ?? string.Empty);
                        if (!string.IsNullOrWhiteSpace(standard))
                        {
                            qso.Freq = standard;
                            note = $"  (frequency set to {standard})";
                        }
                    }
                }

                var dal = DataAccess.GetInstance();
                if (dal == null) return;
                dal.Update(qso);

                // QSO has no change notification, so a cell this method rewrote BEHIND the operator's
                // back (the Band, after a frequency edit) only redraws when the rows are re-read.
                //
                // Deliberately NOT refreshed here: the row is still inside its edit transaction at this
                // point and WPF forbids Items.Refresh() during one. Doing it anyway threw, and the
                // swallowed exception left the cell drawn blank - which looked exactly like the edit
                // had wiped a value. It is done once the row is finished instead.
                _refreshWhenRowDone = true;

                TB_Status.Text = $"Saved: {qso.DXCall}  {qso.Date} {qso.Time}{note}";
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                TB_Status.Text = "Could not save the change: " + ex.Message;
            }
        }

        // Frequency and Band sort by NUMBER, not by text.
        //
        // Left to the default string sort, "14.200000" comes before "7.100000" because '1' precedes
        // '7', and the bands read 10M, 160M, 20M, 40M - which looks like the sort is simply broken.
        // Every other column is genuinely text and keeps the built-in behaviour.
        private void ResultsGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            // Re-sorting moves the rows, so the Shift+click anchor (a position, not a QSO) no longer
            // points at what the operator last clicked. Ticks themselves are untouched.
            _lastPickedIndex = -1;

            string path = e.Column.SortMemberPath;
            bool byBand = path == "Band";
            if (!byBand && path != "Freq") return;   // text column: let the DataGrid handle it

            var view = CollectionViewSource.GetDefaultView(ResultsGrid.ItemsSource) as ListCollectionView;
            if (view == null) return;

            ListSortDirection direction = e.Column.SortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;

            e.Column.SortDirection = direction;
            view.CustomSort = new QsoFrequencyComparer(byBand, direction == ListSortDirection.Ascending);
            e.Handled = true;
        }

        // Orders QSOs by frequency in MHz. For the Band column the band's own representative frequency
        // is used, so the bands come out in wavelength order (160M, 80M, 40M ...) rather than A-Z.
        private class QsoFrequencyComparer : System.Collections.IComparer
        {
            private readonly bool _byBand;
            private readonly int _sign;

            public QsoFrequencyComparer(bool byBand, bool ascending)
            {
                _byBand = byBand;
                _sign = ascending ? 1 : -1;
            }

            public int Compare(object x, object y) =>
                _sign * Value(x as QSO).CompareTo(Value(y as QSO));

            private double Value(QSO q)
            {
                if (q == null) return double.MaxValue;   // blanks sort to the end
                string text = _byBand
                    ? HolyLogParser.convertBandToFreq(q.Band ?? string.Empty)
                    : HolyLogParser.NormalizeFreqToMhz(q.Freq);
                double mhz;
                return double.TryParse(text, System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out mhz)
                    ? mhz
                    : double.MaxValue;
            }
        }

        // Placement is handled by WindowBounds (attached in the constructor); these remain only
        // because the XAML wires them up.
        private void Window_LocationChanged(object sender, EventArgs e) { }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e) { }

        // Custom caption buttons - WindowStyle=None removed the OS ones.
        private void TitleBar_Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

        private void TitleBar_MaxRestore_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
            else SystemCommands.MaximizeWindow(this);
        }

        private void TitleBar_Close_Click(object sender, RoutedEventArgs e) => Close();

        // Keep the maximize/restore glyph in sync with the window state.
        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (TitleBar_MaxRestoreBtn == null) return;
            bool maximized = WindowState == WindowState.Maximized;
            TitleBar_MaxRestoreBtn.Content = maximized ? "" : "";
            TitleBar_MaxRestoreBtn.ToolTip = maximized ? "Restore Down" : "Maximize";
        }
    }

    // Represents one country entry in the dropdown: name + flag image (same PNG assets as
    // StatisticsWindow) + the ARRL entity number, shown at the right of the row.
    public class SearchCountryItem
    {
        private static readonly System.Collections.Generic.Dictionary<string, BitmapImage> _flagCache =
            new System.Collections.Generic.Dictionary<string, BitmapImage>();

        public string      Name      { get; }
        public BitmapImage FlagImage { get; }

        // The ARRL entity number for this country, 0 when no database knows the wording the log used.
        // The award world speaks in these numbers, so the list shows both and either can be typed.
        public int    Code     { get; }
        public string CodeText => Code > 0 ? Code.ToString() : "";

        public SearchCountryItem(string name)
        {
            Name      = name;
            Code      = CodeOf(name);
            FlagImage = GetFlagImage(name);
        }

        // A country whose wording nothing recognises still belongs in the list - it is in the log -
        // it simply shows no number, rather than being hidden or shown as 0.
        private static int CodeOf(string name)
        {
            try { return CountryLookup.Shared.EntityCodeForCountry(name); }
            catch { return 0; }
        }

        // What the editable box shows once a country is picked: the NUMBER FIRST, then the name.
        //
        // The box is one fixed width and several DXCC names are longer than it - "Bonaire, Curacao
        // (Neth Antilles)" among them - so whatever sits at the end is the part that disappears.
        // Putting the number first means the name is what clips, exactly as it already did, and the
        // number stays readable for every country. The search reads the number back out of this.
        public override string ToString() => Code > 0 ? Code + "  " + Name : Name;

        private static BitmapImage GetFlagImage(string countryName)
        {
            if (!MainWindow.DxccNameToIso.TryGetValue(countryName, out string iso)) return null;
            if (_flagCache.TryGetValue(iso, out BitmapImage cached)) return cached;
            try
            {
                var bm = new BitmapImage(new Uri($"pack://application:,,,/Images/flags/{iso}.png"));
                _flagCache[iso] = bm;
                return bm;
            }
            catch { return null; }
        }
    }
}
