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
                    AddSubject(title, level);
                    continue;
                }

                Match picture = Regex.Match(line, @"^\s*!\[(.*?)\]\((.+?)\)\s*$");
                if (picture.Success)
                {
                    endParagraph(); endList();
                    doc.Blocks.Add(BuildPicture(picture.Groups[2].Value.Trim(), picture.Groups[1].Value.Trim()));
                    continue;
                }

                Match numbered = Regex.Match(line, @"^\s*\d+\.\s+(.*)$");
                Match bullet = Regex.Match(line, @"^\s*[-*]\s+(.*)$");
                if (numbered.Success || bullet.Success)
                {
                    endParagraph();
                    TextMarkerStyle style = numbered.Success ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc;
                    if (list == null || list.MarkerStyle != style)
                    {
                        list = new List { MarkerStyle = style, Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(30, 0, 0, 0) };
                        doc.Blocks.Add(list);
                    }
                    var item = new Paragraph { Margin = new Thickness(0, 0, 0, 4) };
                    AddInlines(item.Inlines, (numbered.Success ? numbered : bullet).Groups[1].Value);
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

        private void AddSubject(string title, int level)
        {
            var text = new TextBlock
            {
                Text = title,
                FontSize = 16,
                FontWeight = level == 1 ? FontWeights.Bold : FontWeights.Normal,
                Margin = new Thickness(level == 1 ? 2 : 20, level == 1 ? 6 : 2, 4, 2),
                TextWrapping = TextWrapping.Wrap
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            LB_Subjects.Items.Add(new ListBoxItem { Content = text, Cursor = System.Windows.Input.Cursors.Hand });
        }

        private void Subjects_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int i = LB_Subjects.SelectedIndex;
            if (i < 0 || i >= _headings.Count) return;
            ScrollHeadingToTop(_headings[i]);
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
