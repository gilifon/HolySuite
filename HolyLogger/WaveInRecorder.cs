using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace HolyLogger
{
    // CAPTURES AUDIO FROM A CHOSEN RECORDING DEVICE - the mirror image of WaveOutPlayer.
    //
    // This is what the CW decoder listens to. It is deliberately the plainest thing that can work:
    // the classic winmm waveIn API, so no extra library or DLL travels with the program, and the
    // same P/Invoke style the sending side already uses and has been proven by.
    //
    // NOTHING HERE KNOWS WHAT RADIO IS ON THE OTHER END. To Windows the IC-7610's USB codec is an
    // ordinary recording device with a name, exactly like an IC-7300's, an FT-991A's, a SignaLink
    // or any other outboard interface - and exactly like a microphone held against a loudspeaker,
    // which is how someone with an older radio and no USB at all will use this. The operator picks
    // a device by name; no CAT, no rig model, no driver of our own. That is the whole reason a
    // decoder built on this works for every station, not only for the one it was written on.
    //
    // Threading: one background thread owns the device from open to close and does nothing else.
    // Buffers are polled rather than driven by a waveIn callback - a callback would arrive on a
    // driver thread with rules about what may be called from it, and polling four buffers every few
    // milliseconds costs nothing measurable and cannot deadlock.
    public class WaveInRecorder : IDisposable
    {
        const uint WAVE_MAPPER = 0xFFFFFFFF;
        const uint WHDR_DONE = 0x00000001;
        const int MAXPNAMELEN = 32;
        const ushort WAVE_FORMAT_PCM = 1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WAVEINCAPS
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAXPNAMELEN)]
            public string szPname;
            public uint dwFormats;
            public ushort wChannels;
            public ushort wReserved1;
        }

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

        [DllImport("winmm.dll")] static extern uint waveInGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern uint waveInGetDevCaps(IntPtr deviceID, ref WAVEINCAPS caps, uint cbSize);
        [DllImport("winmm.dll")] static extern uint waveInOpen(out IntPtr hWaveIn, uint deviceID, WAVEFORMATEX fmt, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] static extern uint waveInPrepareHeader(IntPtr hWaveIn, IntPtr hdr, uint cbSize);
        [DllImport("winmm.dll")] static extern uint waveInUnprepareHeader(IntPtr hWaveIn, IntPtr hdr, uint cbSize);
        [DllImport("winmm.dll")] static extern uint waveInAddBuffer(IntPtr hWaveIn, IntPtr hdr, uint cbSize);
        [DllImport("winmm.dll")] static extern uint waveInStart(IntPtr hWaveIn);
        [DllImport("winmm.dll")] static extern uint waveInStop(IntPtr hWaveIn);
        [DllImport("winmm.dll")] static extern uint waveInReset(IntPtr hWaveIn);
        [DllImport("winmm.dll")] static extern uint waveInClose(IntPtr hWaveIn);

        // Friendly names of the recording devices, index order == waveIn device id. The API truncates
        // them to 31 characters, which is still enough to tell "USB Audio CODEC" from "Microphone".
        public static List<string> GetInputDeviceNames()
        {
            var list = new List<string>();
            try
            {
                uint n = waveInGetNumDevs();
                for (uint i = 0; i < n; i++)
                {
                    var caps = new WAVEINCAPS();
                    if (waveInGetDevCaps((IntPtr)i, ref caps, (uint)Marshal.SizeOf(typeof(WAVEINCAPS))) == 0
                        && !string.IsNullOrWhiteSpace(caps.szPname))
                        list.Add(caps.szPname);
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            return list;
        }

        // The waveIn device id for a saved device name, or WAVE_MAPPER (system default) when the name
        // is empty or the device is no longer there - the radio may simply be switched off.
        public static uint ResolveDeviceId(string deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName)) return WAVE_MAPPER;
            try
            {
                uint n = waveInGetNumDevs();
                for (uint i = 0; i < n; i++)
                {
                    var caps = new WAVEINCAPS();
                    if (waveInGetDevCaps((IntPtr)i, ref caps, (uint)Marshal.SizeOf(typeof(WAVEINCAPS))) == 0
                        && string.Equals(caps.szPname, deviceName, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            return WAVE_MAPPER;
        }

        // THE RATES WE ASK FOR, BEST FIRST.
        //
        // 8000 samples a second is plenty for CW: the tone is under 1 kHz and the whole point is to
        // hand the decoder the smallest stream that still carries the note. Windows resamples for us,
        // so nearly every device opens at 8000 whatever the hardware really runs at.
        //
        // The rest of the list is for the device that refuses anyway. Some cheap USB codecs and some
        // virtual cables accept one rate only, and a decoder that gives up on those would be a
        // decoder that works on this station and no other. Whatever opens, ActualSampleRate says what
        // it was, and the decoding stage resamples from there.
        static readonly uint[] PreferredRates = { 8000, 11025, 16000, 22050, 44100, 48000 };

        // A block of about a tenth of a second. Short enough that the level meter looks live and the
        // decoder is never far behind the air; long enough that four of them are no work at all.
        const int BlockMilliseconds = 100;
        const int BufferCount = 4;

        readonly object _gate = new object();
        IntPtr _handle = IntPtr.Zero;
        IntPtr[] _headers;
        GCHandle[] _pins;
        byte[][] _blocks;
        Thread _thread;
        volatile bool _stopping;

        /// <summary>Samples a second the device actually opened at. 0 until Start succeeds.</summary>
        public int ActualSampleRate { get; private set; }

        /// <summary>Name of the device that was opened, as Windows reports it.</summary>
        public string ActualDeviceName { get; private set; }

        /// <summary>True between a successful Start and Stop.</summary>
        public bool IsRunning { get { return _thread != null && !_stopping; } }

        /// <summary>
        /// Loudest sample of the most recent block, 0 to 1. Read it from a timer to drive a meter -
        /// it costs nothing and does not need the block event.
        /// </summary>
        public double Level { get; private set; }

        /// <summary>
        /// One block of mono 16-bit samples, on the capture thread. The array is REUSED for the next
        /// block, so a handler that wants to keep the samples must copy them. count is how many of
        /// the array's entries are real.
        /// </summary>
        public event Action<short[], int> Samples;

        /// <summary>
        /// Capture stopped by itself rather than by Stop() - the device was unplugged, or the radio
        /// was switched off. The message is short enough to put straight on screen.
        /// </summary>
        public event Action<string> Failed;

        /// <summary>
        /// Opens the named device (empty name = the system default) and starts capturing. Returns
        /// false with a plain-words reason when the device cannot be opened, which is the usual case
        /// worth telling the operator about: another program already holds it.
        /// </summary>
        public bool Start(string deviceName, out string error)
        {
            error = null;
            lock (_gate)
            {
                if (_thread != null) { error = "Already listening."; return false; }

                uint deviceId = ResolveDeviceId(deviceName);
                ActualDeviceName = string.IsNullOrWhiteSpace(deviceName) ? "System default" : deviceName;

                IntPtr h = IntPtr.Zero;
                uint openedRate = 0;
                uint lastResult = 0;

                foreach (uint rate in PreferredRates)
                {
                    var fmt = new WAVEFORMATEX
                    {
                        wFormatTag = WAVE_FORMAT_PCM,
                        nChannels = 1,
                        nSamplesPerSec = rate,
                        nAvgBytesPerSec = rate * 2,
                        nBlockAlign = 2,
                        wBitsPerSample = 16,
                        cbSize = 0
                    };

                    lastResult = waveInOpen(out h, deviceId, fmt, IntPtr.Zero, IntPtr.Zero, 0);
                    if (lastResult == 0 && h != IntPtr.Zero) { openedRate = rate; break; }
                    h = IntPtr.Zero;
                }

                if (h == IntPtr.Zero)
                {
                    // 4 is MMSYSERR_ALLOCATED. It is by far the most likely failure and the only one
                    // the operator can do anything about, so it gets its own sentence instead of a
                    // number he would have to look up.
                    error = lastResult == 4
                        ? "That device is already being used by another program."
                        : "Could not open that device for recording.";
                    return false;
                }

                _handle = h;
                ActualSampleRate = (int)openedRate;

                int blockBytes = (int)(openedRate * 2 * BlockMilliseconds / 1000);
                _blocks = new byte[BufferCount][];
                _pins = new GCHandle[BufferCount];
                _headers = new IntPtr[BufferCount];
                int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));

                for (int i = 0; i < BufferCount; i++)
                {
                    _blocks[i] = new byte[blockBytes];
                    _pins[i] = GCHandle.Alloc(_blocks[i], GCHandleType.Pinned);

                    // The header lives in unmanaged memory on purpose: the driver writes into it
                    // while we are elsewhere, and a struct on the managed heap is free to move.
                    _headers[i] = Marshal.AllocHGlobal(hdrSize);
                    var hdr = new WAVEHDR
                    {
                        lpData = _pins[i].AddrOfPinnedObject(),
                        dwBufferLength = (uint)blockBytes
                    };
                    Marshal.StructureToPtr(hdr, _headers[i], false);

                    if (waveInPrepareHeader(_handle, _headers[i], (uint)hdrSize) != 0 ||
                        waveInAddBuffer(_handle, _headers[i], (uint)hdrSize) != 0)
                    {
                        ReleaseUnderLock();
                        error = "Could not set up the recording buffers.";
                        return false;
                    }
                }

                if (waveInStart(_handle) != 0)
                {
                    ReleaseUnderLock();
                    error = "The device would not start recording.";
                    return false;
                }

                _stopping = false;
                _thread = new Thread(CaptureLoop)
                {
                    IsBackground = true,
                    Name = "HolyLogger audio capture"
                };
                _thread.Start();
                return true;
            }
        }

        public void Stop()
        {
            Thread toJoin;
            lock (_gate)
            {
                if (_thread == null) return;
                _stopping = true;
                toJoin = _thread;
            }

            // Outside the lock: the capture thread takes the same lock when it hands a block over.
            try { if (!toJoin.Join(1500)) Log.Swallow(new TimeoutException("Audio capture thread did not stop in time.")); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }

            lock (_gate)
            {
                ReleaseUnderLock();
                _thread = null;
            }
        }

        void CaptureLoop()
        {
            int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
            short[] samples = null;

            try
            {
                while (!_stopping)
                {
                    bool gotAnything = false;

                    for (int i = 0; i < BufferCount && !_stopping; i++)
                    {
                        IntPtr hdrPtr;
                        lock (_gate)
                        {
                            if (_headers == null || _handle == IntPtr.Zero) return;
                            hdrPtr = _headers[i];
                        }

                        var hdr = (WAVEHDR)Marshal.PtrToStructure(hdrPtr, typeof(WAVEHDR));
                        if ((hdr.dwFlags & WHDR_DONE) == 0) continue;

                        gotAnything = true;
                        int bytes = (int)hdr.dwBytesRecorded;
                        int count = bytes / 2;

                        if (count > 0)
                        {
                            if (samples == null || samples.Length < count) samples = new short[count];

                            // The bytes are already in the pinned managed array, so this is a plain
                            // copy inside managed memory - no marshalling.
                            Buffer.BlockCopy(_blocks[i], 0, samples, 0, bytes);

                            int peak = 0;
                            for (int s = 0; s < count; s++)
                            {
                                int v = samples[s];
                                if (v < 0) v = -v;
                                if (v > peak) peak = v;
                            }
                            Level = peak / 32768.0;

                            var handler = Samples;
                            if (handler != null)
                            {
                                try { handler(samples, count); }
                                catch (Exception swallowed) { Log.Swallow(swallowed); }
                            }
                        }

                        // Hand the buffer back for the next block. Unprepare then prepare again is the
                        // documented way round: re-adding a header that still carries WHDR_DONE is
                        // refused by some drivers.
                        lock (_gate)
                        {
                            if (_stopping || _handle == IntPtr.Zero) return;
                            waveInUnprepareHeader(_handle, hdrPtr, (uint)hdrSize);

                            var reset = new WAVEHDR
                            {
                                lpData = _pins[i].AddrOfPinnedObject(),
                                dwBufferLength = (uint)_blocks[i].Length
                            };
                            Marshal.StructureToPtr(reset, hdrPtr, false);

                            if (waveInPrepareHeader(_handle, hdrPtr, (uint)hdrSize) != 0 ||
                                waveInAddBuffer(_handle, hdrPtr, (uint)hdrSize) != 0)
                            {
                                RaiseFailed("The recording device stopped responding.");
                                return;
                            }
                        }
                    }

                    // Nothing was ready: wait a fraction of a block rather than spin.
                    if (!gotAnything) Thread.Sleep(10);
                }
            }
            catch (Exception ex)
            {
                Log.Swallow(ex);
                RaiseFailed("Listening stopped because of an error.");
            }
        }

        void RaiseFailed(string message)
        {
            var handler = Failed;
            if (handler == null) return;
            try { handler(message); }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        // Closes the device and gives every buffer back. Safe to call twice; called from Stop and
        // from a half-finished Start.
        void ReleaseUnderLock()
        {
            if (_handle != IntPtr.Zero)
            {
                try { waveInStop(_handle); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                try { waveInReset(_handle); } catch (Exception swallowed) { Log.Swallow(swallowed); }
            }

            if (_headers != null)
            {
                int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
                for (int i = 0; i < _headers.Length; i++)
                {
                    if (_headers[i] == IntPtr.Zero) continue;
                    if (_handle != IntPtr.Zero)
                        try { waveInUnprepareHeader(_handle, _headers[i], (uint)hdrSize); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                    try { Marshal.FreeHGlobal(_headers[i]); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                    _headers[i] = IntPtr.Zero;
                }
                _headers = null;
            }

            if (_pins != null)
            {
                for (int i = 0; i < _pins.Length; i++)
                    if (_pins[i].IsAllocated) _pins[i].Free();
                _pins = null;
            }

            _blocks = null;

            if (_handle != IntPtr.Zero)
            {
                try { waveInClose(_handle); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                _handle = IntPtr.Zero;
            }

            Level = 0;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
