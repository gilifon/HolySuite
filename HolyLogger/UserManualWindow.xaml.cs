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

namespace HolyLogger
{
    // HELP > USER MANUAL (user, 2026-11-12). The text is Help/UserManual.md, built into the program, in
    // a small piece of Markdown: "# " a subject, "## " a part of it, "1. " numbered steps, "- " a bullet,
    // **bold**, and a web address becomes a link. Everything else is a paragraph; lines that follow one
    // another without a blank line between them are one paragraph.
    //
    // The list on the left is made from the headings, so a new subject needs nothing but its heading
    // and its text in the file. Clicking one puts that heading at the top of the text on the right.
    public partial class UserManualWindow : Window
    {
        private const string ResourceName = "HolyLogger.Help.UserManual.md";

        private readonly List<Paragraph> _headings = new List<Paragraph>();

        public UserManualWindow()
        {
            InitializeComponent();
            RestoreSize();
            Closing += (s, e) => SaveSize();

            FD_Viewer.Document = BuildDocument(ReadManual());

            // ROOM BELOW THE LAST SUBJECT. Without it the text ends before a subject near the end can
            // reach the top, and clicking it only scrolls to the bottom. A window's height of blank
            // space after the end lets every subject be brought to the top.
            FD_Viewer.SizeChanged += (s, e) =>
            {
                var d = FD_Viewer.Document;
                if (d != null) d.PagePadding = new Thickness(20, 12, 20, Math.Max(40, FD_Viewer.ActualHeight - 60));
            };
        }

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
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 16,
                PagePadding = new Thickness(20, 12, 20, 40),
                TextAlignment = TextAlignment.Left
            };
            doc.SetResourceReference(FlowDocument.ForegroundProperty, "TextBrush");

            var paragraphLines = new List<string>();
            List list = null;

            Action endParagraph = () =>
            {
                if (paragraphLines.Count == 0) return;
                var p = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
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
                        FontWeight = FontWeights.Bold,
                        FontSize = level == 1 ? 26 : 20,
                        Margin = new Thickness(0, doc.Blocks.Count == 0 ? 0 : (level == 1 ? 28 : 18), 0, 8),
                        Tag = level
                    };
                    doc.Blocks.Add(h);
                    _headings.Add(h);
                    AddSubject(title, level);
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
                        list = new List { MarkerStyle = style, Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(28, 0, 0, 0) };
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

        // BringIntoView only promises "somewhere on screen", which for a heading near the end leaves it at
        // the bottom with its text out of sight. So the heading's own position is measured and the text
        // scrolled by exactly that much: the heading lands at the top.
        private void ScrollHeadingToTop(Paragraph heading)
        {
            try
            {
                var sv = FindChild<ScrollViewer>(FD_Viewer);
                if (sv == null) { heading.BringIntoView(); return; }

                Rect r = heading.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                if (r.IsEmpty) { heading.BringIntoView(); return; }
                sv.ScrollToVerticalOffset(Math.Max(0, sv.VerticalOffset + r.Top - 8));
            }
            catch (Exception ex) { Log.Swallow(ex); }
        }

        private static T FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T) return (T)child;
                var deeper = FindChild<T>(child);
                if (deeper != null) return deeper;
            }
            return null;
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
