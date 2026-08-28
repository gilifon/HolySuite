using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HolyLogger
{
    // HELP > SUPPORT > SEND MAIL TO THE DEVELOPERS.
    //
    // Three fields and a Send button. The error log is attached every time - including when the message
    // is a question or a suggestion and nothing has gone wrong - because the operator cannot be expected
    // to judge whether it is relevant, and the one occasion it is missing is the occasion it was needed.
    //
    // HOW IT LEAVES THE MACHINE. Not through the operator's own mail: most amateurs read their mail in a
    // browser, and no program can drive a web page's compose window or attach a file to it. The message
    // is posted to HolyLogger's own endpoint, which mails it on. Until that endpoint is known, Send
    // packs the whole thing - message, log and any pasted pictures - into ONE zip on the Desktop, so the
    // operator has a single thing to attach by hand rather than a path to go hunting for.
    public partial class SupportMailWindow : Window
    {
        // FILLED IN WHEN THE SERVER IS AGREED. One place, deliberately: the window does not care which
        // of the two it is doing, and neither does the operator.
        private const string PostEndpoint = "https://tools.iarc.org/Holyland/server/sendmail.php";
        private const string DeveloperAddress = "holylogger@iarc.org";   // only shown if the post is not used

        // Pictures pasted into the message, kept as files rather than pushed into the text: an image
        // inside a body of text has to be got back out again at the other end, and what the developer
        // wants is the picture itself.
        private readonly List<string> _pictures = new List<string>();
        private string _pictureFolder;

        public SupportMailWindow(string callsignDefault = null)
        {
            InitializeComponent();

            // Typed once, then filled in forever after. The callsign falls back to the station's own,
            // which the operator has already entered today.
            TB_Name.Text = (Properties.Settings.Default.SupportSenderName ?? string.Empty).Trim();
            TB_Email.Text = (Properties.Settings.Default.SupportSenderEmail ?? string.Empty).Trim();
            TB_Callsign.Text = (callsignDefault ?? string.Empty).Trim();

            string log = Log.FilePath;
            bool haveLog = !string.IsNullOrEmpty(log) && File.Exists(log);
            TB_Attachment.Text = haveLog
                ? "Your error log will be attached automatically (" + SizeText(log) + "). It lists what went " +
                  "wrong inside HolyLogger and when - no passwords, and nothing you typed into a QSO."
                : "There is no error log on this machine yet, so the message will be sent on its own.";

            TB_Body.PreviewKeyDown += Body_PreviewKeyDown;

            // Send stays off until all five are filled. Checked on every keystroke rather than on the
            // press: a button that refuses to work says what is missing before the operator commits to
            // sending, not after.
            TB_Name.TextChanged += AnyRequiredFieldChanged;
            TB_Callsign.TextChanged += AnyRequiredFieldChanged;
            TB_Email.TextChanged += AnyRequiredFieldChanged;
            TB_Subject.TextChanged += AnyRequiredFieldChanged;
            TB_Body.TextChanged += AnyRequiredFieldChanged;
            UpdateSendEnabled();

            // Land on the first thing still empty, so a returning operator starts at the subject.
            Loaded += (s, e) => FirstEmpty().Focus();
        }

        private void AnyRequiredFieldChanged(object sender, TextChangedEventArgs e)
        {
            UpdateSendEnabled();
        }

        private void UpdateSendEnabled()
        {
            bool ready = TB_Name.Text.Trim().Length > 0
                      && TB_Callsign.Text.Trim().Length > 0
                      && TB_Email.Text.Trim().Length > 0
                      && TB_Subject.Text.Trim().Length > 0
                      && TB_Body.Text.Trim().Length > 0;

            Btn_Send.IsEnabled = ready;
            TB_Status.Text = ready ? "" : "The fields marked * have to be filled in.";
        }

        private Control FirstEmpty()
        {
            if (TB_Name.Text.Trim().Length == 0) return TB_Name;
            if (TB_Callsign.Text.Trim().Length == 0) return TB_Callsign;
            if (TB_Email.Text.Trim().Length == 0) return TB_Email;
            return TB_Subject;
        }

        private static string SizeText(string path)
        {
            try
            {
                long bytes = new FileInfo(path).Length;
                return bytes < 1024 ? bytes + " bytes" : Math.Round(bytes / 1024.0) + " KB";
            }
            catch (Exception ex) { Log.Swallow(ex); return "unknown size"; }
        }

        // ── pasting a picture ─────────────────────────────────────────────

        // Ctrl+V with a picture on the clipboard. A TextBox would simply refuse it - there is no text to
        // paste - and the operator would conclude the program cannot take screenshots. It can.
        private void Body_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.V || (Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;

            try
            {
                if (Clipboard.ContainsImage())
                {
                    BitmapSource img = Clipboard.GetImage();
                    if (img != null && AddPicture(img)) e.Handled = true;   // don't also paste text
                    return;
                }

                // A picture file copied in Explorer counts too - it is the same intention.
                if (Clipboard.ContainsFileDropList())
                {
                    bool took = false;
                    foreach (string f in Clipboard.GetFileDropList())
                    {
                        if (string.IsNullOrEmpty(f) || !File.Exists(f)) continue;
                        string ext = Path.GetExtension(f).ToLowerInvariant();
                        if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".gif" && ext != ".bmp") continue;
                        if (CopyPicture(f)) took = true;
                    }
                    if (took) { RefreshPictures(); e.Handled = true; }
                }
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                TB_Status.Text = "That picture could not be read.";
            }
        }

        private string PictureFolder()
        {
            if (_pictureFolder == null)
            {
                _pictureFolder = Path.Combine(Path.GetTempPath(),
                    "HolyLogger-support-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                Directory.CreateDirectory(_pictureFolder);
            }
            return _pictureFolder;
        }

        // The server takes six. Refusing the seventh here, with a word about it, is kinder than letting
        // the whole message be rejected after it has been written.
        private const int MaxPictures = 6;

        private bool AtPictureLimit()
        {
            if (_pictures.Count < MaxPictures) return false;
            TB_Status.Text = "That is as many pictures as one message can carry (" + MaxPictures + ").";
            return true;
        }

        private bool AddPicture(BitmapSource img)
        {
            if (AtPictureLimit()) return true;   // handled: the paste is not passed on as text either
            try
            {
                string file = Path.Combine(PictureFolder(), "picture-" + (_pictures.Count + 1) + ".png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(img));
                using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write))
                    encoder.Save(fs);

                _pictures.Add(file);
                RefreshPictures();
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("A pasted picture could not be saved: " + ex.GetType().Name + ": " + ex.Message);
                TB_Status.Text = "That picture could not be saved.";
                return false;
            }
        }

        private bool CopyPicture(string source)
        {
            if (AtPictureLimit()) return false;
            try
            {
                string file = Path.Combine(PictureFolder(),
                    "picture-" + (_pictures.Count + 1) + Path.GetExtension(source).ToLowerInvariant());
                File.Copy(source, file, true);
                _pictures.Add(file);
                return true;
            }
            catch (Exception ex) { Log.Swallow(ex); return false; }
        }

        // The strip of thumbnails under the message, each with its own way out. Rebuilt whole rather
        // than patched: there are never more than a handful, and a list that is rebuilt cannot drift
        // out of step with the files it is showing.
        private void RefreshPictures()
        {
            IC_Pictures.Items.Clear();

            for (int i = 0; i < _pictures.Count; i++)
            {
                string path = _pictures[i];
                var panel = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };

                var image = new Image { Height = 96, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 4) };
                try
                {
                    var bm = new BitmapImage();
                    bm.BeginInit();
                    bm.CacheOption = BitmapCacheOption.OnLoad;      // so the file is not held open
                    bm.DecodePixelHeight = 96;                      // decoded at the size it is shown
                    bm.UriSource = new Uri(path);
                    bm.EndInit();
                    if (bm.CanFreeze) bm.Freeze();
                    image.Source = bm;
                }
                catch (Exception ex) { Log.Swallow(ex); }

                var remove = new Button
                {
                    Content = "Remove",
                    FontSize = 16,
                    Height = 30,
                    Tag = path
                };
                remove.Click += RemovePicture_Click;

                panel.Children.Add(image);
                panel.Children.Add(remove);
                IC_Pictures.Items.Add(panel);
            }

            PicturesPanel.Visibility = _pictures.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            TB_PicturesHeader.Text = _pictures.Count == 1
                ? "1 picture will be sent with your message"
                : _pictures.Count + " pictures will be sent with your message";
            UpdateSendEnabled();   // not just "clear the status" - the reminder must survive a paste
        }

        private void RemovePicture_Click(object sender, RoutedEventArgs e)
        {
            string path = (sender as Button)?.Tag as string;
            if (string.IsNullOrEmpty(path)) return;
            _pictures.Remove(path);
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) { Log.Swallow(ex); }
            RefreshPictures();
        }

        // ── sending ───────────────────────────────────────────────────────

        private void Btn_Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private async void Btn_Send_Click(object sender, RoutedEventArgs e)
        {
            string name = (TB_Name.Text ?? string.Empty).Trim();
            string callsign = (TB_Callsign.Text ?? string.Empty).Trim();
            string email = (TB_Email.Text ?? string.Empty).Trim();
            string subject = (TB_Subject.Text ?? string.Empty).Trim();
            string body = (TB_Body.Text ?? string.Empty).Trim();

            if (name.Length == 0) { Complain("Please put your name in, so we know who wrote.", TB_Name); return; }
            if (callsign.Length == 0) { Complain("Please put your callsign in.", TB_Callsign); return; }
            if (email.Length == 0) { Complain("Please put your email address in - without it there is nowhere to send the answer.", TB_Email); return; }
            if (!LooksLikeAnAddress(email)) { Complain("That does not look like an email address. Please check it - the answer goes there.", TB_Email); return; }
            if (subject.Length == 0) { Complain("Please give the message a subject.", TB_Subject); return; }
            if (body.Length == 0) { Complain("Please write the message itself - the subject alone is not enough to answer.", TB_Body); return; }

            // Remembered only once the operator has actually sent something with them.
            try
            {
                Properties.Settings.Default.SupportSenderName = name;
                Properties.Settings.Default.SupportSenderEmail = email;
                Properties.Settings.Default.Save();
            }
            catch (Exception ex) { Log.Swallow(ex); }

            if (PostEndpoint.Length > 0)
            {
                await SendToServerAsync(name, callsign, email, subject, body);
                return;
            }

            PackOneFileForTheDesktop(name, callsign, email, subject, body);
        }

        // POSTS THE MESSAGE TO HolyLogger's OWN ENDPOINT, which mails it on. The server's rules, which
        // this has to satisfy exactly or the message is refused:
        //
        //   callsign, name, email, title, message   all required, none may be empty
        //   attachment                              required, one file, name ending .txt - the error log
        //   images[]                                optional, up to 6, PNG/JPEG/GIF/BMP - the pasted
        //                                           screenshots, shown inside the mail and attached
        //   sizes                                   message 10,000 characters; log 5 MB; each picture
        //                                           5 MB; everything together 20 MB
        //   answer                                  JSON - {"success":true} or {"success":false,"error":…}
        //
        // The message is what the mail is MADE of, so it is required here too - it was briefly optional,
        // on the reasoning that a subject line can be the whole point, and it cannot. The attachment must
        // always exist, so a machine with no error log yet sends a file saying so, which is a true and
        // useful thing for the developer to receive.
        private async System.Threading.Tasks.Task SendToServerAsync(
            string name, string callsign, string email, string subject, string body)
        {
            SetSending(true);
            try
            {
                string message = ComposeMessage(body);
                byte[] attachment;
                string attachmentName;
                BuildAttachment(out attachment, out attachmentName);

                string answer;
                bool ok;

                using (var content = new MultipartFormDataContent())
                {
                    content.Add(new StringContent(callsign, Encoding.UTF8), "callsign");
                    content.Add(new StringContent(name, Encoding.UTF8), "name");
                    content.Add(new StringContent(email, Encoding.UTF8), "email");
                    content.Add(new StringContent(subject, Encoding.UTF8), "title");
                    content.Add(new StringContent(message, Encoding.UTF8), "message");

                    var file = new ByteArrayContent(attachment);
                    file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
                    content.Add(file, "attachment", attachmentName);

                    // THE PASTED PICTURES, as images[] - the square brackets are what makes PHP present
                    // them as a list rather than keeping only the last one. They are shown inside the
                    // mail, in this order, and attached as well.
                    foreach (string picture in _pictures)
                    {
                        try
                        {
                            if (!File.Exists(picture)) continue;
                            var bytes = File.ReadAllBytes(picture);
                            if (bytes.Length == 0) continue;

                            var image = new ByteArrayContent(bytes);
                            image.Headers.ContentType =
                                new System.Net.Http.Headers.MediaTypeHeaderValue(MimeOf(picture));
                            content.Add(image, "images[]", Path.GetFileName(picture));
                        }
                        catch (Exception ex) { Log.Swallow(ex); }
                    }

                    // Task.Run, as everywhere else that talks to the network here: on .NET Framework the
                    // proxy is resolved on the thread that STARTS the request, and this one starts on the
                    // UI thread from a button press.
                    HttpResponseMessage response = await System.Threading.Tasks.Task.Run(
                        () => SupportHttp.PostAsync(PostEndpoint, content)).ConfigureAwait(true);

                    answer = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
                    ok = response.IsSuccessStatusCode && SaysSuccess(answer);
                }

                if (ok)
                {
                    HolyMessageBox.ShowSuccess(
                        "Your message has been sent." +
                        (_pictures.Count > 0
                            ? "\n\n" + (_pictures.Count == 1 ? "Your picture went" : "Your " + _pictures.Count + " pictures went")
                              + " with it."
                            : string.Empty) +
                        "\n\nThe answer will go to " + email + ".",
                        "Sent", this);
                    Close();
                    return;
                }

                // The server says WHY in plain words; showing our own guess instead would be worse.
                string why = ErrorFrom(answer);
                Log.Warn("The support message was refused: " + (why ?? "(no reason given)") + " | " + Trim(answer, 300));
                HolyMessageBox.ShowWarning(
                    "The message was not sent.\n\n" + (why ?? "The service did not accept it.") +
                    "\n\nYou can try again, or press Cancel and use Help > Support > Open the error log.",
                    "Not sent", this);
            }
            catch (Exception ex)
            {
                Log.Warn("The support message could not be sent: " + ex.GetType().Name + ": " + ex.Message);
                HolyMessageBox.ShowError(
                    "The message could not be sent:\n\n" + ex.Message +
                    "\n\nCheck that you are online and try again.",
                    "Not sent", this);
            }
            finally
            {
                SetSending(false);
            }
        }

        // One client for the window's lifetime, with a timeout of its own: the default 100 seconds is a
        // very long time to watch a button say "Sending…".
        private static readonly HttpClient SupportHttp =
            new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        private void SetSending(bool sending)
        {
            _sending = sending;
            Btn_Send.IsEnabled = !sending;
            Btn_Cancel.IsEnabled = !sending;
            TB_Name.IsEnabled = TB_Callsign.IsEnabled = TB_Email.IsEnabled =
                TB_Subject.IsEnabled = TB_Body.IsEnabled = !sending;
            TB_Status.Text = sending ? "Sending…" : string.Empty;
            Cursor = sending ? System.Windows.Input.Cursors.Wait : null;
            if (!sending) UpdateSendEnabled();
        }

        private bool _sending;

        // The message the server receives: what was typed, plus the two facts every support message
        // needs and no operator should have to look up.
        private string ComposeMessage(string body)
        {
            var sb = new StringBuilder();
            sb.AppendLine(body);
            sb.AppendLine();
            // At the FOOT of the message, behind a line the reader cannot miss: what the operator wrote
            // is the message, and this is a footnote to it. Never above, where it would push their own
            // words down the page.
            sb.AppendLine("==============================================");
            sb.AppendLine("THIS MACHINE AND THIS LOG");
            sb.AppendLine("==============================================");
            sb.Append(MachineReport());

            string message = sb.ToString();
            const int ServerLimit = 10000;
            return message.Length <= ServerLimit ? message : message.Substring(0, ServerLimit);
        }

        // The error log, sent as a .txt because that is the only kind of file the service takes. Trimmed
        // to its LAST 4 MB if it has grown past the 5 MB the service accepts: the end of a log is the
        // part that describes what just went wrong.
        private void BuildAttachment(out byte[] bytes, out string fileName)
        {
            fileName = "holylogger-log.txt";
            const int ServerLimit = 5 * 1024 * 1024;
            const int KeepBytes = 4 * 1024 * 1024;

            string path = Log.FilePath;
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    var info = new FileInfo(path);
                    if (info.Length <= ServerLimit)
                    {
                        bytes = File.ReadAllBytes(path);
                        if (bytes.Length > 0) return;
                    }
                    else
                    {
                        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            fs.Seek(-KeepBytes, SeekOrigin.End);
                            var tail = new byte[KeepBytes];
                            int read = fs.Read(tail, 0, KeepBytes);
                            var head = Encoding.UTF8.GetBytes(
                                "(the earlier part of this log was left out - it is over the size the service accepts)\r\n\r\n");
                            bytes = new byte[head.Length + read];
                            Buffer.BlockCopy(head, 0, bytes, 0, head.Length);
                            Buffer.BlockCopy(tail, 0, bytes, head.Length, read);
                            return;
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Swallow(ex); }

            // No log, or it could not be read. The service demands a file, and "there is no log on this
            // machine" is worth knowing anyway - it means nothing has gone wrong that was written down.
            bytes = Encoding.UTF8.GetBytes(
                "There is no error log on this machine, or it could not be read." + Environment.NewLine +
                "HolyLogger " + VersionText() + " on " + Environment.OSVersion.VersionString + Environment.NewLine);
        }

        // WHAT MACHINE IS THIS, AND WHAT IS IT BEING ASKED TO DO? A report of "it is slow" means nothing
        // without both halves: a 28,000-QSO log on a laptop with 2 GB free is a different program from a
        // 300-QSO log on a new desktop, and the code cannot be improved for machines nobody can name.
        //
        // The last four lines are the ones that will actually teach us something - the size of the log,
        // where its database sits, and whether that place is a network drive or a folder some cloud
        // service is synchronising underneath it. SQLite on a synced or networked file is the single
        // commonest cause of a logger that "went slow for no reason".
        //
        // What is deliberately NOT sent: the Windows user name, any file path, and anything at all about
        // other software on the machine. None of it would help and all of it is theirs.
        private static string MachineReport()
        {
            var sb = new StringBuilder();
            try
            {
                // ONE SUBJECT TO A LINE. Two facts on one line read as one fact and the second is
                // skipped - and the whole point of this block is that somebody's eye catches the line
                // that explains the complaint.
                sb.AppendLine("HolyLogger:     " + VersionText());
                sb.AppendLine("Windows:        " + WindowsName()
                              + ", " + (Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit"));
                sb.AppendLine(".NET:           " + DotNetVersion());
                sb.AppendLine("CPU:            " + CpuName());
                sb.AppendLine("Cores:          " + Environment.ProcessorCount);
                sb.AppendLine("RAM:            " + MemoryLine());
                sb.AppendLine("Screen:         " + ScreenLine());

                var dal = DataAccess.GetInstance();
                sb.AppendLine("Database:       " + DatabaseLine(dal != null ? dal.DbPath : null));
                sb.AppendLine("Logs:           " + LogCountLine(dal));
                sb.AppendLine("Active log:     " + ActiveLogLine(dal));
                sb.AppendLine("Whole database: " + WholeDatabaseLine(dal));
                sb.AppendLine("Memory in use:  "
                              + (System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024))
                              + " MB");
            }
            catch (Exception ex) { Log.Swallow(ex); sb.AppendLine("(the machine details could not be read)"); }
            return sb.ToString();
        }

        // "Windows 10 22H2 (19045)". The friendly name lives in the registry; OSVersion alone reports
        // only the build.
        private static string WindowsName()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                           @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (key == null) return Environment.OSVersion.VersionString;
                    string name = key.GetValue("ProductName") as string;
                    string display = (key.GetValue("DisplayVersion") as string)
                                     ?? (key.GetValue("ReleaseId") as string);
                    string build = key.GetValue("CurrentBuild") as string;

                    // Windows 11 keeps "Windows 10" in ProductName; the build number is what tells them
                    // apart, and getting this wrong in a support mail wastes somebody's afternoon.
                    int b;
                    if (!string.IsNullOrEmpty(name) && int.TryParse(build, out b) && b >= 22000)
                        name = name.Replace("Windows 10", "Windows 11");

                    return (name ?? "Windows")
                           + (string.IsNullOrEmpty(display) ? "" : " " + display)
                           + (string.IsNullOrEmpty(build) ? "" : " (" + build + ")");
                }
            }
            catch (Exception ex) { Log.Swallow(ex); return Environment.OSVersion.VersionString; }
        }

        // The installed .NET Framework, from the release number Microsoft documents.
        private static string DotNetVersion()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                           @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    if (key == null) return "4.x";
                    object release = key.GetValue("Release");
                    if (release == null) return "4.x";
                    int r = Convert.ToInt32(release);
                    if (r >= 533320) return "4.8.1";
                    if (r >= 528040) return "4.8";
                    if (r >= 461808) return "4.7.2";
                    if (r >= 460798) return "4.7";
                    if (r >= 394802) return "4.6.2";
                    return "4.x (" + r + ")";
                }
            }
            catch (Exception ex) { Log.Swallow(ex); return "4.x"; }
        }

        // Read from the registry rather than asked of WMI: the same answer, without the pause WMI takes.
        private static string CpuName()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                           @"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    string name = key == null ? null : key.GetValue("ProcessorNameString") as string;
                    return string.IsNullOrWhiteSpace(name) ? "unknown" : name.Trim();
                }
            }
            catch (Exception ex) { Log.Swallow(ex); return "unknown"; }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                         ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([System.Runtime.InteropServices.In, System.Runtime.InteropServices.Out] MEMORYSTATUSEX lpBuffer);

        // How much there is and how much is left. The second number is the one that matters: a machine
        // with 16 GB and 300 MB free behaves like a machine with no memory at all.
        private static string MemoryLine()
        {
            try
            {
                var m = new MEMORYSTATUSEX();
                if (!GlobalMemoryStatusEx(m)) return "unknown";
                return (m.ullTotalPhys / (1024 * 1024 * 1024)) + " GB ("
                       + Math.Round(m.ullAvailPhys / 1024.0 / 1024 / 1024, 1) + " GB free)";
            }
            catch (Exception ex) { Log.Swallow(ex); return "unknown"; }
        }

        // Size and scaling both: a 4K screen at 200% is a very different amount of drawing from a 1080p
        // one, and this program draws two large tables.
        private static string ScreenLine()
        {
            try
            {
                double w = SystemParameters.PrimaryScreenWidth;
                double h = SystemParameters.PrimaryScreenHeight;

                // Scaling from whichever window is actually on screen. Asking Application.Current
                // .MainWindow alone answered "unknown" whenever there wasn't one - which is every time
                // this is measured outside the running program, and would have been a permanent blank
                // in the report if it ever ran a moment early.
                int scale = 100;
                foreach (Window w2 in Application.Current != null ? Application.Current.Windows : new WindowCollection())
                {
                    var src = System.Windows.PresentationSource.FromVisual(w2);
                    if (src == null || src.CompositionTarget == null) continue;
                    scale = (int)Math.Round(src.CompositionTarget.TransformToDevice.M11 * 100);
                    break;
                }

                // More than one screen, without WinForms: the virtual desktop is wider or taller than
                // the primary screen exactly when there is another one beside or above it.
                bool several = SystemParameters.VirtualScreenWidth > w + 1
                            || SystemParameters.VirtualScreenHeight > h + 1;

                return (int)w + "x" + (int)h + ", " + scale + "% scaling"
                       + (several ? ", more than one screen" : "");
            }
            catch (Exception ex) { Log.Swallow(ex); return "unknown"; }
        }

        // The database's size, the KIND of drive it sits on, and whether something is synchronising the
        // folder. A log on a network share or inside a synced folder is the commonest reason a logger
        // that was fast becomes slow, and no amount of reading our own code would ever reveal it.
        private static string DatabaseLine(string dbPath)
        {
            try
            {
                if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath)) return "not found";

                var info = new FileInfo(dbPath);
                string size = info.Length >= 1024 * 1024
                    ? Math.Round(info.Length / 1024.0 / 1024) + " MB"
                    : Math.Round(info.Length / 1024.0) + " KB";

                string where = "unknown drive";
                long freeGb = -1;
                try
                {
                    var drive = new DriveInfo(Path.GetPathRoot(info.FullName));
                    where = drive.DriveType.ToString().ToLowerInvariant() + " disk";
                    if (drive.IsReady) freeGb = drive.AvailableFreeSpace / (1024 * 1024 * 1024);
                }
                catch (Exception ex) { Log.Swallow(ex); }

                // The folder is named only by the SERVICE synchronising it, never by its path.
                string synced = null;
                string lower = info.FullName.ToLowerInvariant();
                if (lower.Contains("onedrive")) synced = "OneDrive";
                else if (lower.Contains("dropbox")) synced = "Dropbox";
                else if (lower.Contains("google drive") || lower.Contains("googledrive")) synced = "Google Drive";
                else if (lower.Contains("icloud")) synced = "iCloud";

                return size + " on a " + where
                       + (freeGb >= 0 ? " (" + freeGb + " GB free)" : "")
                       + (synced != null ? "  — inside a " + synced + " folder" : "");
            }
            catch (Exception ex) { Log.Swallow(ex); return "unknown"; }
        }

        private static string LogCountLine(DataAccess dal)
        {
            try
            {
                var logs = dal != null ? dal.GetLogs() : null;
                return logs == null ? "unknown" : logs.Count.ToString();
            }
            catch (Exception ex) { Log.Swallow(ex); return "unknown"; }
        }

        private static string ActiveLogLine(DataAccess dal)
        {
            try
            {
                return dal == null ? "unknown"
                                   : dal.GetQsoCountForLog(dal.ActiveLogId).ToString("N0") + " QSOs";
            }
            catch (Exception ex) { Log.Swallow(ex); return "unknown"; }
        }

        private static string WholeDatabaseLine(DataAccess dal)
        {
            try
            {
                return dal == null ? "unknown" : dal.GetQsoCount().ToString("N0") + " QSOs";
            }
            catch (Exception ex) { Log.Swallow(ex); return "unknown"; }
        }

        // What the server is told the picture is. It checks the bytes itself and does not trust this,
        // but sending "application/octet-stream" for a PNG invites it to say no.
        private static string MimeOf(string path)
        {
            switch (Path.GetExtension(path ?? string.Empty).ToLowerInvariant())
            {
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif":  return "image/gif";
                case ".bmp":  return "image/bmp";
                default:      return "image/png";   // what Ctrl+V produces
            }
        }

        private static bool SaysSuccess(string json)
        {
            try
            {
                var o = Newtonsoft.Json.Linq.JObject.Parse(json ?? string.Empty);
                var v = o["success"] as Newtonsoft.Json.Linq.JValue;
                return v != null && v.Type == Newtonsoft.Json.Linq.JTokenType.Boolean && (bool)v.Value;
            }
            catch (Exception ex) { Log.Swallow(ex); return false; }
        }

        private static string ErrorFrom(string json)
        {
            try
            {
                var o = Newtonsoft.Json.Linq.JObject.Parse(json ?? string.Empty);
                string err = (string)o["error"];
                return string.IsNullOrWhiteSpace(err) ? null : err;
            }
            catch (Exception ex) { Log.Swallow(ex); return null; }
        }

        private static string Trim(string s, int max)
        {
            s = s ?? string.Empty;
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        // Deliberately loose: something, an @, something, a dot, something. A stricter rule rejects
        // addresses that are perfectly real, and the only cost of letting a wrong one through is a reply
        // that bounces - which is exactly what happens today with no address field at all.
        private static bool LooksLikeAnAddress(string s)
        {
            int at = s.IndexOf('@');
            if (at <= 0 || at == s.Length - 1) return false;
            if (s.IndexOf('@', at + 1) >= 0) return false;
            string domain = s.Substring(at + 1);
            int dot = domain.IndexOf('.');
            return dot > 0 && dot < domain.Length - 1 && s.IndexOf(' ') < 0;
        }

        private void Complain(string what, Control focus)
        {
            HolyMessageBox.Show(what, "Not sent yet", HolyMsgType.Info, this);
            focus.Focus();
        }

        // THE FALLBACK, while there is no server to post to. One zip so there is exactly one thing to
        // attach: who wrote it, what they said, which build they are on, the log, and every picture.
        // Shown in Explorer straight away - a file the operator never sees is a file they never send.
        private void PackOneFileForTheDesktop(string name, string callsign, string email, string subject, string body)
        {
            string staging = null;
            try
            {
                staging = Path.Combine(Path.GetTempPath(), "HolyLogger-message-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);

                var sb = new StringBuilder();
                sb.AppendLine("HolyLogger - message to the developers");
                sb.AppendLine("Name:     " + name);
                sb.AppendLine("Callsign: " + callsign);
                sb.AppendLine("Email:    " + email);
                sb.AppendLine("Subject:  " + subject);
                sb.AppendLine("Written:  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("Version:  " + VersionText());
                sb.AppendLine("Windows:  " + Environment.OSVersion.VersionString);
                sb.AppendLine("Pictures: " + _pictures.Count);
                sb.AppendLine();
                sb.AppendLine(body);
                File.WriteAllText(Path.Combine(staging, "message.txt"), sb.ToString(), Encoding.UTF8);

                string log = Log.FilePath;
                if (!string.IsNullOrEmpty(log) && File.Exists(log))
                {
                    try { File.Copy(log, Path.Combine(staging, "holylogger.log"), true); }
                    catch (Exception ex) { Log.Swallow(ex); }
                }

                foreach (string p in _pictures)
                {
                    try { if (File.Exists(p)) File.Copy(p, Path.Combine(staging, Path.GetFileName(p)), true); }
                    catch (Exception ex) { Log.Swallow(ex); }
                }

                string zip = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    "HolyLogger-message-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".zip");
                if (File.Exists(zip)) File.Delete(zip);
                System.IO.Compression.ZipFile.CreateFromDirectory(staging, zip);

                string address = DeveloperAddress.Length > 0 ? DeveloperAddress : "the HolyLogger address";
                HolyMessageBox.Show(
                    "Your message is ready, saved on your Desktop as:\n\n" +
                    Path.GetFileName(zip) + "\n\n" +
                    "Attach that one file to an email to " + address + ". Everything is inside it - your " +
                    "message, the error log and your pictures - so there is nothing else to look for.",
                    "Ready to send", HolyMsgType.Info, this);

                try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + zip + "\""); }
                catch (Exception ex) { Log.Swallow(ex); }

                Close();
            }
            catch (Exception ex)
            {
                Log.Warn("The support message could not be packed: " + ex.GetType().Name + ": " + ex.Message);
                HolyMessageBox.ShowWarning(
                    "The message could not be saved.\n\n" + ex.Message + "\n\n"
                    + "Your text is still on screen — copy it somewhere safe before you close "
                    + "this window.",
                    "Not sent", this);
            }
            finally
            {
                try { if (staging != null && Directory.Exists(staging)) Directory.Delete(staging, true); }
                catch (Exception ex) { Log.Swallow(ex); }
            }
        }

        private static string VersionText()
        {
            try { return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(); }
            catch (Exception ex) { Log.Swallow(ex); return "unknown"; }
        }

        // The pasted pictures live in a temporary folder; once the window is gone they are of no use to
        // anybody. Failing to clean up is not worth telling the operator about.
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            try { if (_pictureFolder != null && Directory.Exists(_pictureFolder)) Directory.Delete(_pictureFolder, true); }
            catch (Exception ex) { Log.Swallow(ex); }
        }
    }
}
