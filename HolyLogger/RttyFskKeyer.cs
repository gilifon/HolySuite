using System;
using System.Collections.Generic;

namespace HolyLogger
{
    /// <summary>
    /// SENDS RTTY BY FSK: the radio sits in its own RTTY mode and makes the two tones itself, and
    /// HolyLogger tells it, bit by bit, which one - by holding one line of a COM port (DTR or RTS) on
    /// or off. On the IC-7610 that is the radio's second USB port, chosen in its menu under USB Keying
    /// (RTTY); any radio with an FSK input can be keyed the same way through a small interface.
    ///
    /// The bits are timed by HolyLogger, the way MMTTY's EXTFSK does it: 45.45 baud is 22 ms a bit, and
    /// Windows' ordinary timers are far too coarse for that. So, like the CW port keyer (PortCwKeyer),
    /// the keying runs on its own thread at the highest priority with the system timer at 1 ms, sleeps
    /// most of each wait and spins the last of it, and places every bit on a schedule measured from the
    /// start of the transmission, so a late wake-up never adds up along a message.
    ///
    /// TRANSMIT ON AND OFF is not this class's business - the line only says mark or space. The main
    /// window switches the radio over CAT when BusyChanged says sending starts and ends, and this class
    /// gives the radio time to change over: a lead of steady mark before the first character, and a
    /// tail after the last.
    ///
    /// WHAT IS SENT: 5-bit Baudot (ITA2, see RttyBaudot), one start bit, five data bits first bit
    /// first, 1.5 stop bits.
    /// </summary>
    public sealed class RttyFskKeyer : IRttySender
    {
        public const double Baud = 45.45;
        const double StopBits = 1.5;
        const double LeadMs = 300;      // steady mark after transmit goes on, before the first character
        const double TailMs = 150;      // and after the last, before transmit goes off


        readonly System.IO.Ports.SerialPort _port;
        readonly bool _useRts;
        readonly bool _markIsOn;
        readonly object _gate = new object();
        readonly Queue<int> _waiting = new Queue<int>();     // Baudot codes still to go
        readonly System.Threading.AutoResetEvent _wake = new System.Threading.AutoResetEvent(false);
        readonly System.Threading.Thread _thread;
        readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        volatile bool _closing;
        volatile int _stopGen;
        volatile bool _busy;

        // The shift the far end is in, as far as we have told it: 0 letters, 1 figures, 2 not known
        // (just after a space in figures - he may have dropped back to letters on it).
        int _shift = 2;

        public string PortName { get; private set; }
        public string Line { get { return _useRts ? "RTS" : "DTR"; } }
        public string Describe { get { return PortName + " " + Line; } }

        /// <summary>
        /// Raised on the keying thread: true when sending starts (switch the radio to transmit now - the
        /// lead of mark is already running), false once the tail is over (switch it back).
        /// </summary>
        public event Action<bool> BusyChanged;

        /// <summary>A character has just gone out whole, on the keying thread - for showing what was sent.</summary>
        public event Action<char> CharacterSent;

        /// <summary>Every change of the line, on the keying thread: mark (true) or space, and the ms on the keyer's clock. For tests.</summary>
        public event Action<bool, double> Edge;

        public bool Busy { get { return _busy; } }

        RttyFskKeyer(System.IO.Ports.SerialPort port, bool useRts, bool markIsOn)
        {
            _port = port;
            _useRts = useRts;
            _markIsOn = markIsOn;
            PortName = port == null ? "(no port)" : port.PortName;
            SetLine(true);
            _thread = new System.Threading.Thread(Run)
            {
                IsBackground = true,
                Priority = System.Threading.ThreadPriority.Highest,
                Name = "RTTY keying on " + PortName
            };
            _thread.Start();
        }

        /// <summary>
        /// Opens the port and sets the line to mark. markIsOn: the line held ON means mark (the swap in
        /// Options turns this round for a radio that takes it the other way). Null and a plain reason
        /// when the port cannot be opened.
        /// </summary>
        public static RttyFskKeyer Open(string portName, bool useRts, bool markIsOn, out string whyNot)
        {
            whyNot = null;
            System.IO.Ports.SerialPort port = null;
            try
            {
                // Both lines off as it opens, so nothing the radio watches twitches on the way in.
                port = new System.IO.Ports.SerialPort(portName)
                {
                    DtrEnable = false,
                    RtsEnable = false,
                    Handshake = System.IO.Ports.Handshake.None
                };
                port.Open();
                return new RttyFskKeyer(port, useRts, markIsOn);
            }
            catch (UnauthorizedAccessException) { whyNot = portName + " is being used by another program."; }
            catch (System.IO.IOException) { whyNot = portName + " is not there - is the cable plugged in?"; }
            catch (Exception ex) { whyNot = portName + " could not be opened: " + ex.Message; }
            try { if (port != null) port.Dispose(); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            return null;
        }

        /// <summary>The whole keyer with no port behind it - for measuring the timing.</summary>
        public static RttyFskKeyer WithoutPort()
        {
            return new RttyFskKeyer(null, false, false);
        }

        /// <summary>Hands text over to go after whatever is already waiting. False once closed.</summary>
        public bool Send(string text)
        {
            if (_closing) return false;
            if (string.IsNullOrEmpty(text)) return true;
            lock (_gate)
            {
                bool fresh = !_busy && _waiting.Count == 0;
                if (fresh) { _waiting.Enqueue(RttyBaudot.LTRS); _waiting.Enqueue(RttyBaudot.LTRS); _shift = RttyBaudot.Letters; }
                foreach (char raw in text) RttyBaudot.Encode(raw, ref _shift, _waiting);
            }
            _wake.Set();
            return true;
        }

        /// <summary>Stops now: whatever is waiting is thrown away, the line goes back to mark, and BusyChanged(false) follows.</summary>
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
            SetLine(true);
            try { if (_port != null) { _port.Close(); _port.Dispose(); } } catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // ---- the keying thread ------------------------------------------------------------------

        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        static extern uint timeBeginPeriod(uint ms);

        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        static extern uint timeEndPeriod(uint ms);

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
            catch (Exception ex) { Log.Warn("RTTY keying on " + PortName + " stopped: " + ex.Message); }
            finally
            {
                SetLine(true);
                if (_busy) { _busy = false; RaiseBusy(false); }
                try { timeEndPeriod(1); } catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
        }

        // One transmission: the lead, every character as it comes - text added meanwhile goes in the
        // same transmission - then the tail.
        void Transmission()
        {
            int gen = _stopGen;
            _busy = true;
            RaiseBusy(true);
            double bit = 1000.0 / Baud;
            double at = _clock.Elapsed.TotalMilliseconds;
            SetLine(true);
            at += LeadMs;

            while (gen == _stopGen && !_closing)
            {
                int code;
                lock (_gate)
                {
                    if (_waiting.Count == 0) break;
                    code = _waiting.Dequeue();
                }
                // Never in the past: text typed after a pause starts now, not where the schedule was.
                at = Math.Max(at, _clock.Elapsed.TotalMilliseconds);
                if (!Bit(false, ref at, bit, gen)) break;                      // start: space
                bool whole = true;
                for (int k = 4; k >= 0 && whole; k--) whole = Bit(((code >> k) & 1) != 0, ref at, bit, gen);
                if (!whole || !Bit(true, ref at, bit * StopBits, gen)) break;  // stop: mark
                RaiseSent(code);
            }

            SetLine(true);
            if (gen == _stopGen && !_closing) WaitUntil(at + TailMs, gen);
            SetLine(true);
            _busy = false;
            RaiseBusy(false);
        }

        // Holds the line at mark or space from `at` for `length` ms; false if stopped meanwhile.
        bool Bit(bool mark, ref double at, double length, int gen)
        {
            if (!WaitUntil(at, gen)) return false;
            SetLine(mark);
            at += length;
            return true;
        }

        bool WaitUntil(double targetMs, int gen)
        {
            while (true)
            {
                if (gen != _stopGen || _closing) return false;
                double left = targetMs - _clock.Elapsed.TotalMilliseconds;
                if (left <= 0) return true;
                if (left > 3) System.Threading.Thread.Sleep((int)(left - 2));
                else System.Threading.Thread.SpinWait(200);
            }
        }

        bool _lineMark = true;
        bool _lineKnown;

        void SetLine(bool mark)
        {
            if (_lineKnown && mark == _lineMark) return;
            _lineKnown = true;
            _lineMark = mark;
            try
            {
                if (_port != null && _port.IsOpen)
                {
                    bool on = mark == _markIsOn;
                    if (_useRts) _port.RtsEnable = on; else _port.DtrEnable = on;
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            var handler = Edge;
            if (handler != null) try { handler(mark, _clock.Elapsed.TotalMilliseconds); } catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        void RaiseBusy(bool busy)
        {
            var handler = BusyChanged;
            if (handler != null) try { handler(busy); } catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // The shift as the far end will print it: tracked here on the keying thread, separately from
        // the encoder's, so a stop part-way never leaves the two disagreeing about what was sent.
        int _sentShift;

        void RaiseSent(int code)
        {
            char c = RttyBaudot.Print(code, ref _sentShift);
            if (c == '\0') return;
            var handler = CharacterSent;
            if (handler != null) try { handler(c); } catch (Exception swallowed) { Log.Swallow(swallowed); }
        }
    }
}
