using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace HolyLogger
{
    /// <summary>
    /// Interaction logic for AboutWindow.xamlyes
    /// 
    /// </summary>
    public partial class AboutWindow : Window
    {
        // callsignCount is what the program is ACTUALLY holding - the callsigns it can suggest from,
        // counted in memory rather than taken from anything the file or the server claims. Negative
        // (or zero) means the big list has not finished loading yet, which is worth saying plainly
        // rather than showing a nought.
        public AboutWindow(int callsignVersion = 0, int callsignCount = -1)
        {
            InitializeComponent();
            Left = (System.Windows.SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = (System.Windows.SystemParameters.PrimaryScreenHeight - Height) / 2;
            L_Version.Text = "Version " + Assembly.GetExecutingAssembly().GetName().Version.ToString();
            L_CallsignVersion.Text = "Callsigns Version: " + callsignVersion.ToString();
            L_CallsignCount.Text = callsignCount > 0
                ? "Callsigns in this program: " + callsignCount.ToString("N0")
                : "Callsigns in this program: still loading";
            L_LastUpdate.Text = "Last Update: " + GetLinkerDateTime(Assembly.GetExecutingAssembly()).ToShortDateString();
        }

        public int kuku()
        {
            return 1;
        }

        public static DateTime GetLinkerDateTime(Assembly assembly, TimeZoneInfo tzi = null)
        {
            // Constants related to the Windows PE file format.
            const int PE_HEADER_OFFSET = 60;
            const int LINKER_TIMESTAMP_OFFSET = 8;

            // Discover the base memory address where our assembly is loaded
            var entryModule = assembly.ManifestModule;
            var hMod = Marshal.GetHINSTANCE(entryModule);
            if (hMod == IntPtr.Zero - 1) throw new Exception("Failed to get HINSTANCE.");

            // Read the linker timestamp
            var offset = Marshal.ReadInt32(hMod, PE_HEADER_OFFSET);
            var secondsSince1970 = Marshal.ReadInt32(hMod, offset + LINKER_TIMESTAMP_OFFSET);

            // Convert the timestamp to a DateTime
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var linkTimeUtc = epoch.AddSeconds(secondsSince1970);
            var dt = TimeZoneInfo.ConvertTimeFromUtc(linkTimeUtc, tzi ?? TimeZoneInfo.Local);
            return dt;
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Escape) this.Close();
        }
    }

    // ── WHAT CHANGED IN THIS VERSION ────────────────────────────────────────────────────────────
    //
    // Operators were being handed a new version with no idea what was in it. They were told an update
    // existed, they installed it, and the program looked exactly as it had before - so work that took
    // weeks was invisible, and a fault they had reported and that had been fixed went on being
    // believed broken.
    //
    // The news lives in ONE plain text file beside the installer, in the same distribution repository
    // the update check already reads its Version from:
    //
    //     https://raw.githubusercontent.com/4Z1KD/HolyLogger/master/WhatsNew.txt
    //
    //     == 8.8.9 ==
    //     Log Fixer finds duplicate contacts and removes them.
    //     LoTW now tells you when it disagrees about a country.
    //
    //     == 8.8.8 ==
    //     ...
    //
    // Newest section first. A version with no section of its own is not an error - it simply has
    // nothing to say, and the program stays quiet rather than showing an empty window.
    public static class ReleaseNotes
    {
        public const string Url = "https://raw.githubusercontent.com/4Z1KD/HolyLogger/master/WhatsNew.txt";

        // The whole file. Never throws and never blocks: an operator with no internet must see the
        // program behave exactly as it always did.
        //
        // THREE ANSWERS, NOT TWO, because "there is no news file" and "I could not reach GitHub" are
        // different things and were being reported as one. A repository with no WhatsNew.txt yet
        // answers 404, and telling that operator to "check your internet connection" sends him to
        // look for a fault in his own machine that is not there.
        //
        //   text  - the file
        //   ""    - reached GitHub; there is no news file (or it is empty)
        //   null  - could not reach GitHub at all
        public static async Task<string> FetchAsync()
        {
            try
            {
                using (var http = new System.Net.Http.HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(20);
                    // Task.Run because this is .NET Framework: the proxy and the DNS name are resolved
                    // on whichever thread STARTS the request, and that must never be the UI thread.
                    var reply = await Task.Run(() => http.GetAsync(Url + "?v=" + DateTime.Now.Ticks));

                    if (reply.StatusCode == System.Net.HttpStatusCode.NotFound) return string.Empty;
                    if (!reply.IsSuccessStatusCode) return LocalCopy();

                    string text = await reply.Content.ReadAsStringAsync();
                    return text ?? string.Empty;
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return LocalCopy(); }
        }

        // THE COPY THAT CAME WITH THE PROGRAM, used when GitHub cannot be reached. It is the file as
        // it stood when this version was built - so it always covers this version and everything
        // before it, which is exactly what a first run after an install needs. A newer release's
        // news is the one thing it cannot have, and that is the one thing the operator does not yet
        // need. Without it, installing without internet meant never being told what changed.
        private static string LocalCopy()
        {
            try
            {
                string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(exe)) return null;

                string beside = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(exe), "WhatsNew.txt");
                if (!System.IO.File.Exists(beside)) return null;

                return System.IO.File.ReadAllText(beside);
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return null; }
        }

        // One version's lines. Empty when that version has no section.
        public static string SectionFor(string file, string version)
        {
            foreach (var s in Sections(file))
                if (SameVersion(s.Key, version)) return s.Value;
            return string.Empty;
        }

        // EVERY version newer than the one the operator was running, not merely the newest. Somebody
        // who skips three releases and then updates should be told what happened in all three; showing
        // only the last would silently drop two versions' worth of news.
        public static string Since(string file, string previousVersion)
        {
            var sb = new StringBuilder();
            foreach (var s in Sections(file))
            {
                if (Compare(s.Key, previousVersion) <= 0) continue;
                if (sb.Length > 0) sb.Append("\n");
                sb.Append(s.Key).Append("\n").Append(s.Value.TrimEnd()).Append("\n");
            }
            return sb.ToString().Trim();
        }

        // "== 8.8.9 ==" splits the file. Anything before the first heading is ignored, so a note to
        // whoever edits the file can sit at the top without appearing in the program.
        private static IEnumerable<KeyValuePair<string, string>> Sections(string file)
        {
            var found = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrWhiteSpace(file)) return found;

            string version = null;
            var body = new StringBuilder();
            foreach (string raw in file.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("==") && line.EndsWith("==") && line.Length > 4)
                {
                    if (version != null) found.Add(new KeyValuePair<string, string>(version, body.ToString()));
                    version = line.Trim('=', ' ', '\t');
                    body.Clear();
                }
                else if (version != null)
                {
                    body.AppendLine(raw.TrimEnd());
                }
            }
            if (version != null) found.Add(new KeyValuePair<string, string>(version, body.ToString()));
            return found;
        }

        private static bool SameVersion(string a, string b) { return Compare(a, b) == 0; }

        // Number by number, so 8.8.10 is correctly newer than 8.8.9 - which a text comparison gets
        // exactly backwards.
        public static int Compare(string a, string b)
        {
            int[] x = Parts(a), y = Parts(b);
            for (int i = 0; i < 4; i++)
            {
                if (x[i] != y[i]) return x[i] < y[i] ? -1 : 1;
            }
            return 0;
        }

        private static int[] Parts(string v)
        {
            var n = new int[4];
            if (string.IsNullOrWhiteSpace(v)) return n;
            string[] bits = v.Trim().Split('.');
            for (int i = 0; i < 4 && i < bits.Length; i++)
            {
                int one;
                int.TryParse(new string(bits[i].TakeWhile(char.IsDigit).ToArray()), out one);
                n[i] = one;
            }
            return n;
        }

        // The version of the program that is running.
        public static string CurrentVersion
        {
            get
            {
                try
                {
                    var a = Assembly.GetExecutingAssembly();
                    return System.Diagnostics.FileVersionInfo.GetVersionInfo(a.Location).FileVersion ?? "";
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); return ""; }
            }
        }

        // THE VERSION THIS OPERATOR HAS ALREADY BEEN SHOWN. Kept in a file of its own beside the log
        // database rather than in Properties.Settings, because an upgrading user keeps his old
        // user.config - so a brand-new setting would arrive holding whatever the previous install left
        // behind, and the one thing this must be right about is what he has and has not seen.
        private static string SeenFile
        {
            get { return System.IO.Path.Combine(SeenFolder, "whatsnew.seen"); }
        }

        private static string SeenFolder
        {
            get
            {
                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "4Z1KD", "HolyLogger");
            }
        }

        // WHICH INSTALL THIS OPERATOR HAS ALREADY BEEN GREETED FOR.
        //
        // The version number is not enough. Reinstalling the same version, or repairing it, or being
        // handed a rebuilt MSI that never got its number bumped, all leave the version identical and
        // the news unread. What changes on EVERY install without fail is the program file itself, so
        // the moment it was written IS the flag - set by the installer, in the only way an installer
        // cannot forget to do, simply by putting the file there.
        private static string InstallFile
        {
            get { return System.IO.Path.Combine(SeenFolder, "whatsnew.install"); }
        }

        // The stamp of the program now running: when its own file was last written.
        public static string CurrentInstallStamp
        {
            get
            {
                try
                {
                    string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                    if (string.IsNullOrEmpty(exe) || !System.IO.File.Exists(exe)) return string.Empty;
                    return System.IO.File.GetLastWriteTimeUtc(exe).ToString("O");
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); return string.Empty; }
            }
        }

        public static string SeenInstallStamp
        {
            get
            {
                try
                {
                    return System.IO.File.Exists(InstallFile)
                         ? System.IO.File.ReadAllText(InstallFile).Trim()
                         : "";
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); return ""; }
            }
            set
            {
                try
                {
                    System.IO.Directory.CreateDirectory(SeenFolder);
                    System.IO.File.WriteAllText(InstallFile, value ?? string.Empty);
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
        }

        public static string LastSeenVersion
        {
            get
            {
                try
                {
                    return System.IO.File.Exists(SeenFile)
                         ? System.IO.File.ReadAllText(SeenFile).Trim()
                         : "";
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); return ""; }
            }
            set
            {
                try
                {
                    string dir = System.IO.Path.GetDirectoryName(SeenFile);
                    if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                    System.IO.File.WriteAllText(SeenFile, (value ?? "").Trim());
                }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
        }
    }

    // The window the news is read in. Built in code rather than XAML because it is a heading, a block
    // of text that scrolls, and a button - and a scrolling block of text is the whole requirement:
    // a message box grows downwards until it is taller than the screen, which is precisely what a
    // release with a lot in it would do.
    public class WhatsNewWindow : Window
    {
        public WhatsNewWindow(string version, string notes, Window owner)
        {
            Title = "What's New";
            Width = 720;
            Height = 560;
            MinWidth = 520;
            MinHeight = 320;
            ShowInTaskbar = false;
            WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
            if (owner != null) Owner = owner;
            SetResourceReference(BackgroundProperty, "WindowBg");

            var grid = new Grid { Margin = new Thickness(16, 12, 16, 14) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var heading = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(version) ? "What's new" : "What's new in " + version,
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 10)
            };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            Grid.SetRow(heading, 0);
            grid.Children.Add(heading);

            var body = BuildBody(notes);

            var scroll = new ScrollViewer
            {
                Content = body,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8)
            };
            scroll.SetResourceReference(Control.BorderBrushProperty, "GridLine");
            Grid.SetRow(scroll, 1);
            grid.Children.Add(scroll);

            var close = new Button
            {
                Content = "Close",
                Width = 120,
                Height = 34,
                FontSize = 16,
                IsDefault = true,
                IsCancel = true,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            close.Click += (s, e) => Close();
            Grid.SetRow(close, 2);
            grid.Children.Add(close);

            Content = grid;
            KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
        }

        // THE VERSION HEADINGS ARE HEADINGS, not the literal "== 8.8.9 ==" out of the file. Now that
        // this window shows the whole history, the numbers are how the reader finds his place in it,
        // so they are set bigger and bold with air above them.
        // A BULLET THAT WRAPS KEEPS ITS OWN LEFT EDGE. This was one TextBlock holding the whole file
        // with line breaks in it, so a line too long for the window came back to the far left, under
        // the dash rather than under the words - and a list stopped looking like a list.
        //
        // Each item is now a block of its own: the dash in a narrow column, the words in a column
        // beside it that wraps within itself. Lines the file had already broken by hand are joined
        // back into their item first, so the wrapping is decided by the window's width and not by
        // wherever the text happened to be split when it was written.
        private static Panel BuildBody(string notes)
        {
            var panel = new StackPanel { Margin = new Thickness(2, 2, 12, 2) };

            string text = (notes ?? "").Trim();
            if (text.Length == 0) return panel;

            var items = new List<string>();

            foreach (string raw in text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
            {
                string line = raw.TrimEnd();
                string trimmed = line.Trim();

                // A line that is only the continuation of the item above it - the file wraps its own
                // long entries with a couple of spaces at the front.
                bool continuation = items.Count > 0
                                 && line.Length > 0
                                 && char.IsWhiteSpace(line[0])
                                 && !trimmed.StartsWith("-");

                if (continuation)
                {
                    items[items.Count - 1] = items[items.Count - 1] + " " + trimmed;
                    continue;
                }

                items.Add(trimmed);
            }

            bool first = true;

            foreach (string item in items)
            {
                bool isHeading = item.Length > 4 && item.StartsWith("==") && item.EndsWith("==");

                if (item.Length == 0)
                {
                    panel.Children.Add(new Border { Height = 8 });
                    continue;
                }

                if (isHeading)
                {
                    var head = new TextBlock
                    {
                        Text = item.Trim('=', ' ', '\t'),
                        FontSize = 19,
                        FontWeight = FontWeights.Bold,
                        Margin = new Thickness(0, first ? 0 : 14, 0, 6)
                    };
                    head.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                    panel.Children.Add(head);
                    first = false;
                    continue;
                }

                if (item.StartsWith("-"))
                {
                    panel.Children.Add(BuildBullet(item.Substring(1).Trim()));
                    first = false;
                    continue;
                }

                // "New", "Fixed", "Faster" - the words that head a list.
                var label = new TextBlock
                {
                    Text = item,
                    FontSize = 16,
                    FontWeight = FontWeights.Bold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 2)
                };
                label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                panel.Children.Add(label);
                first = false;
            }

            return panel;
        }

        // THE FIRST FEW WORDS ARE THE SUBJECT, AND THEY ARE BOLD. An operator reads a release
        // looking for the one thing he cares about, so every item names its subject first and that
        // name is set bold: the eye stops on it, and only then reads the sentence after it. The
        // dash is bold too, so the left edge of the list reads as a list.
        //
        // The subject is whatever stands before the first colon - but ONLY when that colon comes
        // early. A colon in the middle of a sentence ("...marks that are filled in when you send:
        // * your Station Callsign") is not a subject, and bolding up to it would put half the item
        // in bold. When there is no early colon nothing is bolded and the item reads as before.
        private const int SubjectMaxChars = 40;
        private const int SubjectMaxWords = 6;

        private static int SubjectLength(string words)
        {
            int colon = words.IndexOf(':');
            if (colon <= 0) return 0;                       // nothing before it
            if (colon + 1 >= words.Length) return 0;        // nothing after it
            if (colon + 1 > SubjectMaxChars) return 0;      // too far in to be a subject

            int count = words.Substring(0, colon)
                             .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
            if (count > SubjectMaxWords) return 0;

            return colon + 1;                               // the colon belongs to the subject
        }

        private static UIElement BuildBullet(string words)
        {
            var dash = new TextBlock
            {
                Text = "\u2013",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                LineHeight = 24,
                Width = 16,
                VerticalAlignment = VerticalAlignment.Top
            };
            dash.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            var body = new TextBlock
            {
                FontSize = 16,
                LineHeight = 24,
                TextWrapping = TextWrapping.Wrap
            };
            body.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            int subject = SubjectLength(words);
            if (subject > 0)
            {
                body.Inlines.Add(new Run(words.Substring(0, subject)) { FontWeight = FontWeights.Bold });
                body.Inlines.Add(new Run(words.Substring(subject)));
            }
            else
            {
                body.Inlines.Add(new Run(words));
            }

            var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 2) };
            DockPanel.SetDock(dash, Dock.Left);
            row.Children.Add(dash);
            row.Children.Add(body);
            return row;
        }

        // Opens the window if there is anything to say, and does nothing at all if there is not.
        // TRUE WHEN THE OPERATOR WAS ACTUALLY SHOWN SOMETHING. The caller needs to know: marking an
        // install as greeted when nothing was on the screen - because GitHub could not be reached -
        // spends the one chance that install had to say what is new.
        public static bool ShowIfAny(Window owner, string version, string notes)
        {
            if (string.IsNullOrWhiteSpace(notes)) return false;
            new WhatsNewWindow(version, notes, owner).ShowDialog();
            return true;
        }
    }
}

