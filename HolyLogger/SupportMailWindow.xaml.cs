using System;
using System.Collections.Generic;
using System.IO;
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
        private const string PostEndpoint = "";            // e.g. https://tools.iarc.org/iarc/Server/support.php
        private const string DeveloperAddress = "";        // shown to the operator in the fallback text

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
            // The message body is NOT among these. The subject alone can carry the whole point.
            bool ready = TB_Name.Text.Trim().Length > 0
                      && TB_Callsign.Text.Trim().Length > 0
                      && TB_Email.Text.Trim().Length > 0
                      && TB_Subject.Text.Trim().Length > 0;

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

        private bool AddPicture(BitmapSource img)
        {
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

        private void Btn_Send_Click(object sender, RoutedEventArgs e)
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
                // The endpoint is known: post it, log and pictures and all. Written when the address is
                // agreed - until then this branch is never taken.
                TB_Status.Text = "Sending…";
                return;
            }

            PackOneFileForTheDesktop(name, callsign, email, subject, body);
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
            MessageBox.Show(this, what, "Not sent yet", MessageBoxButton.OK, MessageBoxImage.Information);
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
                MessageBox.Show(this,
                    "Your message is ready, saved on your Desktop as:\n\n" +
                    Path.GetFileName(zip) + "\n\n" +
                    "Attach that one file to an email to " + address + ". Everything is inside it - your " +
                    "message, the error log and your pictures - so there is nothing else to look for.",
                    "Ready to send", MessageBoxButton.OK, MessageBoxImage.Information);

                try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + zip + "\""); }
                catch (Exception ex) { Log.Swallow(ex); }

                Close();
            }
            catch (Exception ex)
            {
                Log.Warn("The support message could not be packed: " + ex.GetType().Name + ": " + ex.Message);
                MessageBox.Show(this, "The message could not be saved:\n\n" + ex.Message,
                    "Not sent", MessageBoxButton.OK, MessageBoxImage.Warning);
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
