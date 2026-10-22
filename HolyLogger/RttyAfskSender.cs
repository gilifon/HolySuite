using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace HolyLogger
{
    /// <summary>
    /// SENDS RTTY BY AFSK: the two tones are made here and played through a sound card into the radio's
    /// microphone or data input, with the radio on SSB. For any radio without an FSK input - which is
    /// most older ones - and for anyone who keeps the radio on a data mode.
    ///
    /// The tones are 2125 Hz for mark and 2295 Hz for space, the standard pair: on LSB, mark then lands
    /// 170 Hz above space on the air, as RTTY should. On USB they come out the wrong way round, which is
    /// what the swap in Options is for.
    ///
    /// THE SOUND IS MADE AS IT PLAYS, a few milliseconds at a time, into a short queue of buffers the
    /// sound card plays one after another - so text typed while sending joins the same transmission,
    /// and Stop is heard at once. The phase runs on across every change of tone (no clicks), the
    /// frequency glides over about a millisecond, and the sound fades in and out over a few, so the
    /// signal is no wider than it needs to be. A lead of steady mark gives the radio time to change over
    /// before the first character, and a tail after the last.
    ///
    /// Transmit on and off is the main window's job, over CAT, on BusyChanged - as for FSK.
    /// </summary>
    public sealed class RttyAfskSender : IRttySender
    {
        public const double Baud = 45.45;
        public const double MarkHz = 2125, SpaceHz = 2295;
        const double StopBits = 1.5;
        const double LeadMs = 300, TailMs = 150;
        const int Rate = 48000;
        const int BufferSamples = 1024;     // ~21 ms
        const int BufferCount = 8;          // ~170 ms queued: smooth, and still quick to stop

        readonly string _deviceName;
        readonly bool _swap;
        readonly double _level;
        readonly object _gate = new object();
        readonly Queue<int> _waiting = new Queue<int>();
        readonly System.Threading.AutoResetEvent _wake = new System.Threading.AutoResetEvent(false);
        readonly System.Threading.Thread _thread;

        volatile bool _closing;
        volatile int _stopGen;
        volatile bool _busy;
        int _shift = RttyBaudot.Unknown;

        IntPtr _wave = IntPtr.Zero;
        IntPtr[] _headers;
        GCHandle[] _pins;
        readonly short[][] _blocks;

        public bool Busy { get { return _busy; } }
        public string Describe { get { return "sound card " + (string.IsNullOrEmpty(_deviceName) ? "(system default)" : _deviceName); } }

        public event Action<bool> BusyChanged;
        public event Action<char> CharacterSent;

        RttyAfskSender(string deviceName, bool swap, double level)
        {
            _deviceName = deviceName;
            _swap = swap;
            _level = Math.Max(0.02, Math.Min(1, level));
            _blocks = new short[BufferCount][];
            for (int i = 0; i < BufferCount; i++) _blocks[i] = new short[BufferSamples];
            _thread = new System.Threading.Thread(Run)
            {
                IsBackground = true,
                Priority = System.Threading.ThreadPriority.Highest,
                Name = "RTTY AFSK"
            };
            _thread.Start();
        }

        /// <summary>
        /// Opens the named output device (as Windows lists it). Null and a plain reason when it cannot
        /// be opened. swap: mark on the higher tone, for a radio on USB.
        /// </summary>
        public static RttyAfskSender Open(string deviceName, bool swap, double level, out string whyNot)
        {
            whyNot = null;
            var s = new RttyAfskSender(deviceName, swap, level);
            if (!s.OpenDevice(out whyNot)) { s.Dispose(); return null; }
            return s;
        }

        /// <summary>The whole sender with no sound card behind it: the audio is handed to Played instead. For tests.</summary>
        public static RttyAfskSender WithoutDevice(Action<short[]> played)
        {
            var s = new RttyAfskSender(null, false, 0.5);
            s._testSink = played;
            return s;
        }

        Action<short[]> _testSink;

        public bool Send(string text)
        {
            if (_closing) return false;
            if (string.IsNullOrEmpty(text)) return true;
            lock (_gate)
            {
                bool fresh = !_busy && _waiting.Count == 0;
                if (fresh) { _waiting.Enqueue(RttyBaudot.LTRS); _waiting.Enqueue(RttyBaudot.LTRS); _shift = RttyBaudot.Letters; }
                foreach (char c in text) RttyBaudot.Encode(c, ref _shift, _waiting);
            }
            _wake.Set();
            return true;
        }

        public void Stop()
        {
            lock (_gate)
            {
                _waiting.Clear();
                _stopGen++;
            }
            _wake.Set();
        }

        public void Dispose()
        {
            _closing = true;
            Stop();
            try { _thread.Join(1000); } catch (Exception swallowed) { Log.Swallow(swallowed); }
            CloseDevice();
        }

        // ---- the tone ----------------------------------------------------------------------------

        double _phase, _freq = MarkHz, _gain;
        double _samplesLeft;        // of the current element
        bool _elementMark = true;
        long _made;                 // samples made in this transmission
        int _sentShift;
        readonly List<KeyValuePair<long, char>> _due = new List<KeyValuePair<long, char>>();

        // What the generator is doing: the lead, characters, the tail, or finished.
        enum Part { Lead, Text, Tail, Done }
        Part _part;
        int _code = -1, _bitIndex;  // the character being made and which of its 7 elements

        double MarkTone { get { return _swap ? SpaceHz : MarkHz; } }
        double SpaceTone { get { return _swap ? MarkHz : SpaceHz; } }

        void BeginTransmission()
        {
            _part = Part.Lead;
            _elementMark = true;
            _samplesLeft = LeadMs / 1000.0 * Rate;
            _freq = MarkTone;
            _gain = 0;
            _made = 0;
            _code = -1;
            _due.Clear();
            _sentShift = RttyBaudot.Letters;
        }

        // Moves to the next element when the current one is over.
        void NextElement(int gen)
        {
            double bit = Rate / Baud;
            while (_samplesLeft <= 0 && _part != Part.Done)
            {
                if (_part == Part.Tail) { _part = Part.Done; return; }
                if (_code >= 0 && _bitIndex < 7)
                {
                    // start bit, 5 data bits first bit first, stop bit
                    if (_bitIndex == 0) { _elementMark = false; _samplesLeft += bit; }
                    else if (_bitIndex <= 5) { _elementMark = ((_code >> (5 - _bitIndex)) & 1) != 0; _samplesLeft += bit; }
                    else
                    {
                        _elementMark = true;
                        _samplesLeft += bit * StopBits;
                        char c = RttyBaudot.Print(_code, ref _sentShift);
                        if (c != '\0') _due.Add(new KeyValuePair<long, char>(_made + (long)_samplesLeft, c));
                    }
                    _bitIndex++;
                    continue;
                }
                int next = -1;
                lock (_gate)
                {
                    if (gen == _stopGen && _waiting.Count > 0) next = _waiting.Dequeue();
                }
                if (next >= 0) { _part = Part.Text; _code = next; _bitIndex = 0; continue; }
                _part = Part.Tail;
                _code = -1;
                _elementMark = true;
                _samplesLeft += TailMs / 1000.0 * Rate;
            }
        }

        // Fills one buffer; false once the transmission's last sample has been made.
        bool Fill(short[] block, int gen)
        {
            bool stopping = gen != _stopGen || _closing;
            double glide = 1 - Math.Exp(-1.0 / (0.001 * Rate));     // ~1 ms
            double fade = 1.0 / (0.004 * Rate);                      // ~4 ms in and out
            for (int i = 0; i < block.Length; i++)
            {
                if (!stopping) NextElement(gen);
                bool ending = stopping || _part == Part.Done;
                _gain = ending ? Math.Max(0, _gain - fade) : Math.Min(1, _gain + fade);
                double target = _elementMark ? MarkTone : SpaceTone;
                _freq += (target - _freq) * glide;
                _phase += 2 * Math.PI * _freq / Rate;
                if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
                block[i] = (short)(Math.Sin(_phase) * _gain * _level * 30000);
                _samplesLeft -= 1;
                _made++;
            }
            return !((stopping || _part == Part.Done) && _gain <= 0);
        }

        // ---- the sound card ----------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        class WAVEFORMATEX
        {
            public ushort wFormatTag, nChannels;
            public uint nSamplesPerSec, nAvgBytesPerSec;
            public ushort nBlockAlign, wBitsPerSample, cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength, dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags, dwLoops;
            public IntPtr lpNext, reserved;
        }

        const uint WHDR_DONE = 1;
        [DllImport("winmm.dll")] static extern uint waveOutOpen(out IntPtr h, uint id, WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, uint flags);
        [DllImport("winmm.dll")] static extern uint waveOutPrepareHeader(IntPtr h, IntPtr hdr, uint size);
        [DllImport("winmm.dll")] static extern uint waveOutUnprepareHeader(IntPtr h, IntPtr hdr, uint size);
        [DllImport("winmm.dll")] static extern uint waveOutWrite(IntPtr h, IntPtr hdr, uint size);
        [DllImport("winmm.dll")] static extern uint waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] static extern uint waveOutClose(IntPtr h);
        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

        static readonly uint HeaderSize = (uint)Marshal.SizeOf(typeof(WAVEHDR));

        bool OpenDevice(out string whyNot)
        {
            whyNot = null;
            var fmt = new WAVEFORMATEX { wFormatTag = 1, nChannels = 1, nSamplesPerSec = Rate, nAvgBytesPerSec = Rate * 2, nBlockAlign = 2, wBitsPerSample = 16 };
            uint id = WaveOutPlayer.ResolveDeviceId(_deviceName);
            IntPtr h;
            uint r = waveOutOpen(out h, id, fmt, IntPtr.Zero, IntPtr.Zero, 0);
            if (r != 0 || h == IntPtr.Zero)
            {
                whyNot = (string.IsNullOrEmpty(_deviceName) ? "The sound card" : _deviceName) + " could not be opened for sending.";
                return false;
            }
            _wave = h;
            _pins = new GCHandle[BufferCount];
            _headers = new IntPtr[BufferCount];
            for (int i = 0; i < BufferCount; i++)
            {
                _pins[i] = GCHandle.Alloc(_blocks[i], GCHandleType.Pinned);
                _headers[i] = Marshal.AllocHGlobal((int)HeaderSize);
                var hdr = new WAVEHDR { lpData = _pins[i].AddrOfPinnedObject(), dwBufferLength = BufferSamples * 2, dwFlags = WHDR_DONE };
                Marshal.StructureToPtr(hdr, _headers[i], false);
            }
            return true;
        }

        void CloseDevice()
        {
            if (_wave == IntPtr.Zero) return;
            try { waveOutReset(_wave); } catch (Exception swallowed) { Log.Swallow(swallowed); }
            for (int i = 0; i < BufferCount; i++)
            {
                try { waveOutUnprepareHeader(_wave, _headers[i], HeaderSize); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                Marshal.FreeHGlobal(_headers[i]);
                if (_pins[i].IsAllocated) _pins[i].Free();
            }
            try { waveOutClose(_wave); } catch (Exception swallowed) { Log.Swallow(swallowed); }
            _wave = IntPtr.Zero;
        }

        bool Done(int i)
        {
            var hdr = (WAVEHDR)Marshal.PtrToStructure(_headers[i], typeof(WAVEHDR));
            return (hdr.dwFlags & WHDR_DONE) != 0;
        }

        // ---- the sending thread ------------------------------------------------------------------

        void Run()
        {
            try { timeBeginPeriod(1); } catch (Exception swallowed) { Log.Swallow(swallowed); }
            try
            {
                while (!_closing)
                {
                    bool have;
                    lock (_gate) have = _waiting.Count > 0;
                    if (!have) { _wake.WaitOne(50); continue; }
                    Transmission();
                }
            }
            catch (Exception ex) { Log.Warn("RTTY AFSK stopped: " + ex.Message); }
            finally
            {
                if (_busy) { _busy = false; Raise(false); }
                try { timeEndPeriod(1); } catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
        }

        void Transmission()
        {
            int gen = _stopGen;
            _busy = true;
            Raise(true);
            BeginTransmission();
            long played = 0;
            bool more = true;
            var queued = new Queue<int>();       // buffers handed to the card, oldest first

            while (true)
            {
                if (gen != _stopGen && _wave != IntPtr.Zero)
                {
                    // Stopped: what is already queued is thrown away, so it is heard at once.
                    try { waveOutReset(_wave); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                    queued.Clear();
                    break;
                }

                // Buffers the card has finished with: their characters have now been heard.
                while (queued.Count > 0 && (_wave == IntPtr.Zero || Done(queued.Peek())))
                {
                    int done = queued.Dequeue();
                    if (_wave != IntPtr.Zero) waveOutUnprepareHeader(_wave, _headers[done], HeaderSize);
                    played += BufferSamples;
                    for (int k = _due.Count - 1; k >= 0; k--)
                        if (_due[k].Key <= played) { RaiseSent(_due[k].Value); _due.RemoveAt(k); }
                }

                if (!more && queued.Count == 0) break;

                if (more && queued.Count < BufferCount)
                {
                    int free = FreeBuffer(queued);
                    more = Fill(_blocks[free], gen);
                    if (_wave != IntPtr.Zero)
                    {
                        var hdr = new WAVEHDR { lpData = _pins[free].AddrOfPinnedObject(), dwBufferLength = BufferSamples * 2 };
                        Marshal.StructureToPtr(hdr, _headers[free], false);
                        waveOutPrepareHeader(_wave, _headers[free], HeaderSize);
                        waveOutWrite(_wave, _headers[free], HeaderSize);
                    }
                    else if (_testSink != null) _testSink((short[])_blocks[free].Clone());
                    queued.Enqueue(free);
                    continue;
                }
                System.Threading.Thread.Sleep(3);
            }

            // Characters made but not heard (stopped part-way) are not reported.
            _due.Clear();
            _busy = false;
            Raise(false);
        }

        // A buffer not in the card's queue.
        int FreeBuffer(Queue<int> queued)
        {
            for (int i = 0; i < BufferCount; i++)
                if (!queued.Contains(i)) return i;
            return 0;
        }

        void Raise(bool busy)
        {
            var h = BusyChanged;
            if (h != null) try { h(busy); } catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        void RaiseSent(char c)
        {
            var h = CharacterSent;
            if (h != null) try { h(c); } catch (Exception swallowed) { Log.Swallow(swallowed); }
        }
    }
}
