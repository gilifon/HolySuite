using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Newtonsoft.Json;

namespace HolyLogger
{
    // ── THE FOUR SSB MESSAGES THIS PROGRAM PLAYS ITSELF (his design, 2026-11-25) ─────────────────────
    //
    // Until now the Msg buttons in SSB could only tell the radio to play a message recorded INTO the
    // radio - by the one CAT command a few makers document (see VoiceProfileFor in MainWindow). On every
    // other radio they did nothing at all. Now the operator records his four messages here, on the PC,
    // and the buttons key the radio by CAT and play the recording through the sound card that goes to
    // the radio. That works on any radio OmniRig can key.
    //
    // ONE SWITCH FOR ALL FOUR (his choice): the buttons play either these recordings or the radio's own
    // memory, never a mix. The keycaps carry a small picture of which - a computer or an SD card.
    //
    // ONLY FOUR. The CQ / Answer sets belong to CW and the digital modes, not to SSB (his correction).
    //
    // WHERE THEY LIVE: msg1.wav to msg4.wav and settings.json, in an AudioMessages folder next to the
    // log database. Plain 16-bit mono WAV files, so he can play them in any program, or copy them to
    // another PC. The settings are a file of their own rather than Properties.Settings, so the recordings
    // and what they need to play travel together.
    internal static class AudioMessages
    {
        public const int Count = 4;

        // The longest message that may be recorded. A minute is far more than a CQ needs; the cap is there
        // so a Record left running does not fill the disk, and so a message keys the radio for a bounded
        // time.
        public const int MaxSeconds = 60;

        // Voice wants more than the CW decoder's 8000 a second - he listens to these on his speakers. 22050
        // is clean speech and still small; the rest are for a device that refuses it.
        public static readonly uint[] VoiceRates = { 22050, 16000, 44100, 48000, 11025, 8000 };

        public sealed class Config
        {
            public bool UsePc { get; set; }
            public string InputDevice { get; set; } = string.Empty;
            public string RadioDevice { get; set; } = string.Empty;
            public double RadioLevel { get; set; } = 0.5;      // 0 .. 1
            public string SpeakerDevice { get; set; } = string.Empty;
            public bool TrimSilence { get; set; } = true;

            // HOW THE TRANSMITTER IS SWITCHED ON (his request, 2026-11-25) - for radios with no CAT:
            //   CAT  - OmniRig's transmit command (the default; needs CAT working).
            //   PORT - a COM port's RTS or DTR line, through the transistor or opto-coupler in an
            //          interface box to the radio's PTT.
            //   VOX  - nothing at all: the radio's own VOX hears the message and transmits.
            public string PttVia { get; set; } = PttCat;
            public string PttPort { get; set; } = string.Empty;
            public string PttLine { get; set; } = "RTS";

            // THE FILE EACH KEY PLAYS (his request, 2026-11-26): any WAV file he chooses, anywhere -
            // recorded here and saved where he said, or one he picked with Browse. Index 0 is Msg1.
            // Null in a settings file from before this, which is how the old msg1-4.wav get adopted.
            public List<string> MessageFiles { get; set; }

            // Where Save As and Browse open next time: the folder he last saved to or picked from.
            public string LastFolder { get; set; } = string.Empty;

            // The same for the Record / Play buttons over the Msg keys - their own folder, so saving a
            // QSO does not move where the Msg recordings open.
            public string QsoFolder { get; set; } = string.Empty;

            // THE KEYS' NAMES WHILE THEY PLAY PC FILES (his request, 2026-11-26). The radio's memory
            // keeps its own names (VoiceMsgLabelsJson); these are a second set, so switching the source
            // switches the names. Null until first used, when they start as copies of the radio names.
            public List<string> PcButtonNames { get; set; }
        }

        public const string PttCat = "CAT", PttPort = "PORT", PttVox = "VOX";

        public static string PttVia
        {
            get
            {
                string v = (Settings.PttVia ?? string.Empty).Trim().ToUpperInvariant();
                return v == PttPort || v == PttVox ? v : PttCat;
            }
        }

        static Config _config;
        static string _folder;

        /// <summary>Raised when the source switch changes, so the keycaps can redraw their picture.</summary>
        public static event Action SourceChanged;

        public static string Folder
        {
            get
            {
                if (_folder == null)
                {
                    var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location);
                    string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    _folder = Path.Combine(appData, fvi.CompanyName ?? "HolyLogger", fvi.ProductName ?? "HolyLogger", "AudioMessages");
                }
                return _folder;
            }
        }

        static string ConfigPath { get { return Path.Combine(Folder, "settings.json"); } }

        public static Config Settings
        {
            get
            {
                if (_config == null)
                {
                    try
                    {
                        if (File.Exists(ConfigPath))
                            _config = JsonConvert.DeserializeObject<Config>(File.ReadAllText(ConfigPath));
                    }
                    catch (Exception swallowed) { Log.Swallow(swallowed); }
                    if (_config == null) _config = new Config();
                    AdoptOldRecordings(_config);
                }
                return _config;
            }
        }

        public static void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(Settings, Formatting.Indented));
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            // The PLAY button over the Msg keys shows only when the card to the radio is there.
            var saved = SettingsSaved;
            if (saved != null)
            {
                try { saved(); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
        }

        /// <summary>Raised after the settings were written - for example, a new sound card to the radio.</summary>
        public static event Action SettingsSaved;

        /// <summary>True: the Msg buttons play the recordings on this PC. False: the radio's own memory.</summary>
        public static bool UsePc
        {
            get { return Settings.UsePc; }
            set
            {
                if (Settings.UsePc == value) return;
                Settings.UsePc = value;
                SaveSettings();
                var raise = SourceChanged;
                if (raise != null) raise();
            }
        }

        /// <summary>Tells the main window something that decides whether the buttons can send has changed.</summary>
        public static void RaiseChanged()
        {
            var raise = SourceChanged;
            if (raise != null) raise();
        }

        // Where recordings were kept before he chose his own files (2026-11-25 to 26).
        static string OldFilePath(int n) { return Path.Combine(Folder, "msg" + n + ".wav"); }

        // RECORDINGS MADE BEFORE THE KEYS HAD FILES OF THEIR OWN are kept: each becomes its key's file,
        // once. After that an empty entry means he cleared it, and it stays empty.
        static void AdoptOldRecordings(Config cfg)
        {
            if (cfg.MessageFiles != null) { while (cfg.MessageFiles.Count < Count) cfg.MessageFiles.Add(string.Empty); return; }
            cfg.MessageFiles = new List<string>();
            bool any = false;
            for (int n = 1; n <= Count; n++)
            {
                string old = OldFilePath(n);
                bool there = false;
                try { there = File.Exists(old); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                cfg.MessageFiles.Add(there ? old : string.Empty);
                any |= there;
            }
            if (any) SaveSettings();
        }

        // The PC set of names, made on first use as a copy of the radio set so no key goes blank.
        static List<string> PcNames()
        {
            var cfg = Settings;
            if (cfg.PcButtonNames == null)
            {
                cfg.PcButtonNames = new List<string>(CwKeyboardWindow.ReadLabels(CwKeyboardWindow.VoiceMsgLabelsSetting, Count));
                SaveSettings();
            }
            while (cfg.PcButtonNames.Count < Count) cfg.PcButtonNames.Add(string.Empty);
            return cfg.PcButtonNames;
        }

        /// <summary>Key n's name while the keys play PC files.</summary>
        public static string PcName(int n)
        {
            if (n < 1 || n > Count) return string.Empty;
            return (PcNames()[n - 1] ?? string.Empty).Trim();
        }

        public static void SetPcName(int n, string name)
        {
            if (n < 1 || n > Count) return;
            PcNames()[n - 1] = (name ?? string.Empty).Trim();
            SaveSettings();
        }

        /// <summary>The file key n plays, or empty when none is chosen.</summary>
        public static string FilePath(int n)
        {
            var files = Settings.MessageFiles;
            if (n < 1 || n > Count || files == null || files.Count < n) return string.Empty;
            return (files[n - 1] ?? string.Empty).Trim();
        }

        /// <summary>Sets (or, with null / empty, clears) the file key n plays. The file itself is not touched.</summary>
        public static void SetFile(int n, string path)
        {
            if (n < 1 || n > Count) return;
            var files = Settings.MessageFiles;
            while (files.Count < Count) files.Add(string.Empty);
            files[n - 1] = path ?? string.Empty;
            SaveSettings();
        }

        public static bool HasRecording(int n)
        {
            string path = FilePath(n);
            if (path.Length == 0) return false;
            try { return File.Exists(path); }
            catch (Exception swallowed) { Log.Swallow(swallowed); return false; }
        }

        /// <summary>Length of a recording in seconds, or 0 when there is none.</summary>
        public static double LengthSeconds(int n)
        {
            short[] s; int rate;
            if (!Load(n, out s, out rate) || rate <= 0) return 0;
            return s.Length / (double)rate;
        }

        // ── WAV FILES ──────────────────────────────────────────────────────────────────────────────

        /// <summary>Writes a recording to the file he chose.</summary>
        public static bool Save(string path, short[] samples, int rate)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string temp = path + ".tmp";
                using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write))
                using (var w = new BinaryWriter(fs))
                {
                    int dataBytes = samples.Length * 2;
                    w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
                    w.Write(36 + dataBytes);
                    w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
                    w.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                    w.Write(16);
                    w.Write((short)1);          // PCM
                    w.Write((short)1);          // mono
                    w.Write(rate);
                    w.Write(rate * 2);
                    w.Write((short)2);
                    w.Write((short)16);
                    w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
                    w.Write(dataBytes);
                    foreach (short s in samples) w.Write(s);
                }
                // Written to the side and then moved over the old one, so a failed write never leaves
                // half a message behind.
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
                return true;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return false; }
        }

        /// <summary>
        /// Reads a recording as mono 16-bit samples. A file he put there himself in stereo is mixed down;
        /// anything that is not 16-bit PCM is refused.
        /// </summary>
        public static bool Load(int n, out short[] samples, out int rate)
        {
            return Load(FilePath(n), out samples, out rate);
        }

        /// <summary>The same, for any file - Browse asks it whether a file he picked can be played.</summary>
        public static bool Load(string path, out short[] samples, out int rate)
        {
            samples = null; rate = 0;
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                byte[] b = File.ReadAllBytes(path);
                if (b.Length < 12 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F'
                    || b[8] != 'W' || b[9] != 'A' || b[10] != 'V' || b[11] != 'E') return false;

                int channels = 0, bits = 0, format = 0;
                int pos = 12;
                while (pos + 8 <= b.Length)
                {
                    string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
                    int size = BitConverter.ToInt32(b, pos + 4);
                    int body = pos + 8;
                    if (size < 0) break;
                    if (body + size > b.Length) size = b.Length - body;   // a file cut short still plays what it has

                    if (id == "fmt " && size >= 16)
                    {
                        format = BitConverter.ToUInt16(b, body);
                        channels = BitConverter.ToUInt16(b, body + 2);
                        rate = BitConverter.ToInt32(b, body + 4);
                        bits = BitConverter.ToUInt16(b, body + 14);
                    }
                    else if (id == "data")
                    {
                        if (format != 1 || bits != 16 || channels < 1) return false;
                        int frames = size / (2 * channels);
                        samples = new short[frames];
                        for (int i = 0; i < frames; i++)
                        {
                            int sum = 0;
                            for (int c = 0; c < channels; c++)
                                sum += BitConverter.ToInt16(b, body + (i * channels + c) * 2);
                            samples[i] = (short)(sum / channels);
                        }
                        return rate > 0;
                    }
                    pos = body + size + (size & 1);
                }
                return false;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); samples = null; rate = 0; return false; }
        }

        // ── SILENCE AT THE ENDS ───────────────────────────────────────────────────────────────────────
        //
        // The second between pressing Record and starting to speak, and between finishing and pressing
        // Stop, is dead air on the band. Cut off, with a fifth of a second kept each side so the first and
        // last syllables are not clipped. "Quiet" is judged against the recording's own loudest moment, so
        // a soft voice and a loud one are cut the same way.
        public static short[] TrimSilence(short[] s, int rate)
        {
            if (s == null || s.Length == 0 || rate <= 0) return s;
            int peak = 0;
            foreach (short v in s) { int a = Math.Abs((int)v); if (a > peak) peak = a; }
            if (peak < 300) return s;                            // nothing but noise: leave it for him to hear
            int threshold = Math.Max(300, peak / 12);

            int window = Math.Max(1, rate / 50);                 // 20 ms
            int first = -1, last = -1;
            for (int i = 0; i < s.Length; i += window)
            {
                int end = Math.Min(s.Length, i + window), loud = 0;
                for (int j = i; j < end; j++) { int a = Math.Abs((int)s[j]); if (a > loud) loud = a; }
                if (loud >= threshold) { if (first < 0) first = i; last = end; }
            }
            if (first < 0) return s;

            int keep = rate / 5;
            first = Math.Max(0, first - keep);
            last = Math.Min(s.Length, last + keep);
            var trimmed = new short[last - first];
            Array.Copy(s, first, trimmed, 0, trimmed.Length);
            return trimmed;
        }
    }

    // ── PLAYS ONE RECORDING TO ONE SOUND CARD, AND CAN BE STOPPED ────────────────────────────────────
    //
    // WaveOutPlayer plays a file and cannot be stopped or turned down; a message going over the air must
    // be both. The whole clip goes to the device in one buffer - a minute of 22 kHz voice is 2.6 MB - and
    // a background thread waits for the device to say it is done, or for Stop.
    internal sealed class AudioClipPlayer
    {
        const uint WHDR_DONE = 0x00000001;

        [StructLayout(LayoutKind.Sequential)]
        class WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [DllImport("winmm.dll")] static extern uint waveOutOpen(out IntPtr hWaveOut, uint deviceID, WAVEFORMATEX fmt, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] static extern uint waveOutPrepareHeader(IntPtr hWaveOut, IntPtr hdr, uint cbSize);
        [DllImport("winmm.dll")] static extern uint waveOutWrite(IntPtr hWaveOut, IntPtr hdr, uint cbSize);
        [DllImport("winmm.dll")] static extern uint waveOutUnprepareHeader(IntPtr hWaveOut, IntPtr hdr, uint cbSize);
        [DllImport("winmm.dll")] static extern uint waveOutReset(IntPtr hWaveOut);
        [DllImport("winmm.dll")] static extern uint waveOutClose(IntPtr hWaveOut);

        readonly ManualResetEvent _stop = new ManualResetEvent(false);
        volatile bool _finished;

        public bool IsPlaying { get { return !_finished; } }

        AudioClipPlayer() { }

        /// <summary>
        /// Starts playing. level 0..1 scales the samples (1 = as recorded). leadMs of silence goes first -
        /// the time a radio needs to change over to transmit before the first word. onFinished runs once,
        /// on a background thread, when the clip has played out or was stopped. Returns null with a
        /// plain-words reason when the device cannot be opened (onFinished is then not called).
        /// </summary>
        public static AudioClipPlayer Start(short[] samples, int rate, uint deviceId, double level, int leadMs,
                                            Action onFinished, out string error)
        {
            error = null;
            if (samples == null || samples.Length == 0 || rate <= 0) { error = "The recording is empty."; return null; }

            level = Math.Max(0, Math.Min(1, level));
            int lead = Math.Max(0, rate * leadMs / 1000);
            int tail = rate / 20;                                   // 50 ms, so the last word is not cut by the switch back
            byte[] data = new byte[(lead + samples.Length + tail) * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                int v = (int)Math.Round(samples[i] * level);
                if (v > short.MaxValue) v = short.MaxValue; else if (v < short.MinValue) v = short.MinValue;
                int at = (lead + i) * 2;
                data[at] = (byte)(v & 0xFF);
                data[at + 1] = (byte)((v >> 8) & 0xFF);
            }

            var fmt = new WAVEFORMATEX
            {
                wFormatTag = 1, nChannels = 1, nSamplesPerSec = (uint)rate,
                nAvgBytesPerSec = (uint)rate * 2, nBlockAlign = 2, wBitsPerSample = 16, cbSize = 0
            };

            IntPtr hwo;
            uint r = waveOutOpen(out hwo, deviceId, fmt, IntPtr.Zero, IntPtr.Zero, 0);
            if (r != 0 || hwo == IntPtr.Zero)
            {
                error = r == 4 ? "Another program is using this sound card." : "This sound card cannot be opened.";
                return null;
            }

            var player = new AudioClipPlayer
            {
                _onFinished = onFinished,
                _maxMs = data.Length / 2.0 * 1000.0 / rate + 3000
            };
            var thread = new Thread(() => player.Run(hwo, data)) { IsBackground = true, Name = "AudioClipPlayer" };
            thread.Start();
            return player;
        }

        public void Stop() { try { _stop.Set(); } catch (Exception swallowed) { Log.Swallow(swallowed); } }

        void Run(IntPtr hwo, byte[] data)
        {
            GCHandle pin = default(GCHandle);
            IntPtr hdr = IntPtr.Zero;
            bool prepared = false;
            int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
            try
            {
                pin = GCHandle.Alloc(data, GCHandleType.Pinned);
                // The header lives in unmanaged memory: the driver writes its DONE flag into it while we
                // wait, and a managed struct is free to move.
                hdr = Marshal.AllocHGlobal(hdrSize);
                var h = new WAVEHDR { lpData = pin.AddrOfPinnedObject(), dwBufferLength = (uint)data.Length };
                Marshal.StructureToPtr(h, hdr, false);

                if (waveOutPrepareHeader(hwo, hdr, (uint)hdrSize) != 0) return;
                prepared = true;
                if (waveOutWrite(hwo, hdr, (uint)hdrSize) != 0) return;

                // Done, stopped, or the clip's own length and some to spare - a device that never says
                // DONE must not keep the radio keyed.
                var started = DateTime.UtcNow;
                while (true)
                {
                    if (_stop.WaitOne(20)) break;
                    var now = (WAVEHDR)Marshal.PtrToStructure(hdr, typeof(WAVEHDR));
                    if ((now.dwFlags & WHDR_DONE) != 0) break;
                    if ((DateTime.UtcNow - started).TotalMilliseconds > _maxMs) break;
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            finally
            {
                try { waveOutReset(hwo); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                if (prepared) try { waveOutUnprepareHeader(hwo, hdr, (uint)hdrSize); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                try { waveOutClose(hwo); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                if (hdr != IntPtr.Zero) Marshal.FreeHGlobal(hdr);
                if (pin.IsAllocated) pin.Free();
                _finished = true;
                var done = _onFinished;
                if (done != null) try { done(); } catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
        }

        double _maxMs;
        Action _onFinished;
    }
}
