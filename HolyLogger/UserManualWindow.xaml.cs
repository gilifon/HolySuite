using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HolyLogger
{
    // HELP > USER MANUAL (user, 2026-11-12). The text is Help/UserManual.md, built into the program, in
    // a small piece of Markdown: "# " a subject, "## " a part of it, "1. " numbered steps, "- " a bullet,
    // **bold**, and a web address becomes a link. A line of its own like ![caption](file.png) is a
    // picture from Help/Images, set in the text with its caption under it. Everything else is a
    // paragraph; lines that follow one another without a blank line between them are one paragraph.
    //
    // The list on the left is made from the headings, so a new subject needs nothing but its heading
    // and its text in the file. Clicking one puts that heading at the top of the text on the right.
    //
    // LIKE A WORD PAGE (user, 2026-11-12): white sheet, black text, Word's own heading colour, pictures
    // in the text - whatever the colour scheme, as a printed page would be.
    public partial class UserManualWindow : Window
    {
        private const string ResourceName = "HolyLogger.Help.UserManual.md";
        private const string ImageFolder = "pack://application:,,,/HolyLogger;component/Help/Images/";

        // Word's default heading colour and fonts, so the page reads as a Word document.
        private static readonly Brush HeadingBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x2F, 0x54, 0x96)));
        private static readonly Brush CaptionBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x59, 0x59, 0x59)));
        private static readonly FontFamily BodyFont = new FontFamily("Calibri, Segoe UI");
        private static readonly FontFamily HeadingFont = new FontFamily("Calibri Light, Calibri, Segoe UI");

        private readonly List<Paragraph> _headings = new List<Paragraph>();

        public UserManualWindow()
        {
            InitializeComponent();
            RestoreSize();
            Closing += (s, e) => SaveSize();

            RTB_Text.Document = BuildDocument(ReadManual());
            ShowList();

            // ROOM BELOW THE LAST SUBJECT. Without it the text ends before a subject near the end can
            // reach the top, and clicking it only scrolls to the bottom. A window's height of desk below
            // the sheet lets every subject be brought to the top.
            SV_Desk.SizeChanged += (s, e) =>
                Paper.Margin = new Thickness(20, 20, 20, Math.Max(20, SV_Desk.ActualHeight - 80));

            // The text box would keep the wheel to itself (it cannot scroll - the desk does), so the
            // wheel is handed to the desk.
            RTB_Text.PreviewMouseWheel += (s, e) =>
            {
                SV_Desk.ScrollToVerticalOffset(SV_Desk.VerticalOffset - e.Delta);
                e.Handled = true;
            };
        }

        private static Brush Frozen(Brush b) { b.Freeze(); return b; }

        private static string ReadManual()
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
                {
                    if (s == null) return "# User Manual\n\nThe manual is missing from this copy of HolyLogger.";
                    using (var r = new StreamReader(s, Encoding.UTF8))
                        return r.ReadToEnd();
                }
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                return "# User Manual\n\nThe manual could not be read.";
            }
        }

        // ── the document ────────────────────────────────────────────────────────────────

        private FlowDocument BuildDocument(string text)
        {
            var doc = new FlowDocument
            {
                FontFamily = BodyFont,
                FontSize = 17,
                Foreground = Brushes.Black,
                Background = Brushes.White,
                TextAlignment = TextAlignment.Left
            };

            var paragraphLines = new List<string>();
            List list = null;

            Action endParagraph = () =>
            {
                if (paragraphLines.Count == 0) return;
                var p = new Paragraph { Margin = new Thickness(0, 0, 0, 12) };
                AddInlines(p.Inlines, string.Join(" ", paragraphLines));
                doc.Blocks.Add(p);
                paragraphLines.Clear();
            };
            Action endList = () => { list = null; };

            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.TrimEnd();

                if (line.Trim().Length == 0) { endParagraph(); endList(); continue; }

                if (line.StartsWith("#"))
                {
                    endParagraph(); endList();
                    int level = line.StartsWith("##") ? 2 : 1;
                    string title = line.TrimStart('#').Trim();
                    var h = new Paragraph(new Run(title))
                    {
                        FontFamily = HeadingFont,
                        FontSize = level == 1 ? 30 : 22,
                        Foreground = HeadingBrush,
                        Margin = new Thickness(0, doc.Blocks.Count == 0 ? 0 : (level == 1 ? 32 : 20), 0, 8)
                    };
                    doc.Blocks.Add(h);
                    _headings.Add(h);
                    AddSubject(title, level, h);
                    continue;
                }

                // Everything under a heading is what Search looks through for it.
                if (_subjects.Count > 0) _subjects[_subjects.Count - 1].Text.Append(line).Append(' ');

                Match picture = Regex.Match(line, @"^\s*!\[(.*?)\]\((.+?)\)\s*$");
                if (picture.Success)
                {
                    endParagraph(); endList();
                    doc.Blocks.Add(BuildPicture(picture.Groups[2].Value.Trim(), picture.Groups[1].Value.Trim()));
                    continue;
                }

                Match numbered = Regex.Match(line, @"^\s*(\d+)\.\s+(.*)$");
                Match bullet = Regex.Match(line, @"^\s*[-*]\s+(.*)$");
                if (numbered.Success || bullet.Success)
                {
                    endParagraph();
                    TextMarkerStyle style = numbered.Success ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc;
                    if (list == null || list.MarkerStyle != style)
                    {
                        list = new List { MarkerStyle = style, Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(30, 0, 0, 0) };
                        // Steps carry on from the number written: a picture between step 2 and step 3
                        // starts a new list, and it must still begin at 3.
                        int first;
                        if (numbered.Success && int.TryParse(numbered.Groups[1].Value, out first) && first > 1)
                            list.StartIndex = first;
                        doc.Blocks.Add(list);
                    }
                    var item = new Paragraph { Margin = new Thickness(0, 0, 0, 4) };
                    AddInlines(item.Inlines, numbered.Success ? numbered.Groups[2].Value : bullet.Groups[1].Value);
                    list.ListItems.Add(new ListItem(item));
                    continue;
                }

                endList();
                paragraphLines.Add(line.Trim());
            }
            endParagraph();
            return doc;
        }

        // A picture in the text, centred, with a thin frame and its caption under it. Never drawn larger
        // than it really is - a small screenshot blown up goes soft - and made smaller only when the page
        // is narrower than the picture.
        private static Block BuildPicture(string file, string caption)
        {
            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 16) };
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(ImageFolder + file);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();

                var image = new Image
                {
                    Source = bmp,
                    Stretch = Stretch.Uniform,
                    StretchDirection = StretchDirection.DownOnly,
                    MaxWidth = bmp.PixelWidth,
                    SnapsToDevicePixels = true
                };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                panel.Children.Add(new Border
                {
                    Child = image,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xBF, 0xBF, 0xBF)),
                    BorderThickness = new Thickness(1),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                panel.Children.Add(new TextBlock { Text = "[picture missing: " + file + "]", FontSize = 16, Foreground = Brushes.Gray });
            }

            if (caption.Length > 0)
                panel.Children.Add(new TextBlock
                {
                    Text = caption,
                    FontFamily = BodyFont,
                    FontSize = 16,
                    FontStyle = FontStyles.Italic,
                    Foreground = CaptionBrush,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0)
                });

            return new BlockUIContainer(panel);
        }

        // **bold** and web addresses; the rest is plain text.
        private static void AddInlines(InlineCollection into, string text)
        {
            foreach (Match m in Regex.Matches(text, @"\*\*(.+?)\*\*|(https?://[^\s)]+)|([^*h]+|\*|h)"))
            {
                if (m.Groups[1].Success)
                    into.Add(new Bold(new Run(m.Groups[1].Value)));
                else if (m.Groups[2].Success)
                {
                    string url = m.Groups[2].Value.TrimEnd('.', ',');
                    var link = new Hyperlink(new Run(url)) { NavigateUri = new Uri(url) };
                    link.RequestNavigate += (s, e) =>
                    {
                        try { System.Diagnostics.Process.Start(e.Uri.AbsoluteUri); }
                        catch (Exception ex) { Log.Swallow(ex); }
                        e.Handled = true;
                    };
                    into.Add(link);
                    if (url.Length < m.Groups[2].Value.Length)
                        into.Add(new Run(m.Groups[2].Value.Substring(url.Length)));
                }
                else
                    into.Add(new Run(m.Value));
            }
        }

        // ── the subject list ────────────────────────────────────────────────────────────

        // ONLY THE SUBJECTS, AND A SEARCH (user, 2026-11-15): every heading was a line of the list, so the CW
        // Decoder alone, with a part per radio, made it long. Now the list shows the subjects ("# "); clicking one
        // opens its parts ("## ") under it and closes the one open before. Typing in Search shows every subject and
        // part whose heading or text has the words, each part under its subject.
        private sealed class Subject
        {
            public string Title;
            public int Level;                          // 1 a subject, 2 a part of one
            public int Parent;                         // the index of its subject - its own, for a subject
            public Paragraph Heading;
            public readonly StringBuilder Text = new StringBuilder();
        }

        private readonly List<Subject> _subjects = new List<Subject>();
        private int _open = -1;                        // the subject whose parts are shown
        private bool _filling;

        private void AddSubject(string title, int level, Paragraph heading)
        {
            int parent = level == 1 || _subjects.Count == 0 ? _subjects.Count : _subjects[_subjects.Count - 1].Parent;
            _subjects.Add(new Subject { Title = title, Level = level, Parent = parent, Heading = heading });
        }

        private static bool Has(Subject s, string words)
        {
            return s.Title.IndexOf(words, StringComparison.OrdinalIgnoreCase) >= 0
                || s.Text.ToString().IndexOf(words, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ShowList()
        {
            _filling = true;
            try
            {
                LB_Subjects.Items.Clear();
                string words = (TB_Search.Text ?? string.Empty).Trim();
                for (int i = 0; i < _subjects.Count; i++)
                {
                    Subject s = _subjects[i];
                    bool show;
                    if (words.Length == 0) show = s.Level == 1 || s.Parent == _open;
                    else show = Has(s, words) || (s.Level == 1 && _subjects.Exists(p => p.Level == 2 && p.Parent == i && Has(p, words)));
                    if (!show) continue;

                    string mark = s.Level == 1 && words.Length == 0 ? (i == _open ? "▾ " : "▸ ") : string.Empty;
                    var text = new TextBlock
                    {
                        Text = mark + s.Title,
                        FontSize = 16,
                        FontWeight = s.Level == 1 ? FontWeights.Bold : FontWeights.Normal,
                        Margin = new Thickness(s.Level == 1 ? 2 : 20, s.Level == 1 ? 6 : 2, 4, 2),
                        TextWrapping = TextWrapping.Wrap
                    };
                    text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                    LB_Subjects.Items.Add(new ListBoxItem { Content = text, Tag = i, Cursor = System.Windows.Input.Cursors.Hand });
                }

                if (words.Length > 0 && LB_Subjects.Items.Count == 0)
                {
                    var none = new TextBlock { Text = "Nothing found.", FontSize = 16, Margin = new Thickness(2, 6, 4, 2) };
                    none.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                    LB_Subjects.Items.Add(new ListBoxItem { Content = none, IsEnabled = false });
                }
            }
            finally { _filling = false; }
        }

        // A choice in the list (mouse or arrow keys) brings its heading to the top. Opening and closing a subject
        // is the click's alone - see Subjects_Click.
        private void Subjects_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_filling) return;
            var item = LB_Subjects.SelectedItem as ListBoxItem;
            if (item == null || !(item.Tag is int)) return;
            ScrollHeadingToTop(_subjects[(int)item.Tag].Heading);
        }

        // A CLICK ON A SUBJECT OPENS ITS PARTS, AND A CLICK ON THE OPEN ONE CLOSES THEM (user, 2026-11-15: clicking
        // the open subject did not close it). That second click chooses nothing new, so the list never says it
        // happened - the click itself is listened to instead.
        private void Subjects_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var item = ItemsControl.ContainerFromElement(LB_Subjects, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (item == null || !(item.Tag is int)) return;
            int i = (int)item.Tag;
            ScrollHeadingToTop(_subjects[i].Heading);

            if (_subjects[i].Level == 1 && (TB_Search.Text ?? string.Empty).Trim().Length == 0)
            {
                _open = _open == i ? -1 : i;
                // Not while the list is still handling this click: rebuilt afterwards, with the subject still chosen.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ShowList();
                    _filling = true;
                    try
                    {
                        foreach (ListBoxItem it in LB_Subjects.Items)
                            if (it.Tag is int && (int)it.Tag == i) { LB_Subjects.SelectedItem = it; break; }
                    }
                    finally { _filling = false; }
                }));
            }
        }

        private void Search_TextChanged(object sender, TextChangedEventArgs e)
        {
            ShowList();
        }

        private void Search_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Escape && TB_Search.Text.Length > 0)
            {
                TB_Search.Text = string.Empty;
                e.Handled = true;
            }
        }

        // The heading's own position is measured and the desk scrolled by exactly that much, so the
        // heading lands at the top rather than "somewhere on screen".
        private void ScrollHeadingToTop(Paragraph heading)
        {
            try
            {
                // The first subject shows the sheet from its top edge, as a document opens.
                if (_headings.Count > 0 && heading == _headings[0]) { SV_Desk.ScrollToTop(); return; }

                Rect r = heading.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                if (r.IsEmpty) { heading.BringIntoView(); return; }
                Point inDesk = RTB_Text.TranslatePoint(new Point(0, r.Top), SV_Desk);
                SV_Desk.ScrollToVerticalOffset(Math.Max(0, SV_Desk.VerticalOffset + inDesk.Y - 16));
            }
            catch (Exception ex) { Log.Swallow(ex); }
        }

        // ── size (never position: the window opens over HolyLogger, on whichever screen that is) ──

        private void RestoreSize()
        {
            var s = Properties.Settings.Default;
            if (s.UserManualWindowWidth >= MinWidth) Width = s.UserManualWindowWidth;
            if (s.UserManualWindowHeight >= MinHeight) Height = s.UserManualWindowHeight;
        }

        private void SaveSize()
        {
            try
            {
                var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
                var s = Properties.Settings.Default;
                if (b.Width > 0) s.UserManualWindowWidth = b.Width;
                if (b.Height > 0) s.UserManualWindowHeight = b.Height;
                s.Save();
            }
            catch (Exception ex) { Log.Swallow(ex); }
        }
    }
}
