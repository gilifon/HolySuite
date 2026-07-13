using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace HolyLogger
{
    // Plays a WAV file to a SPECIFIC output device, chosen by the user, regardless of the Windows
    // default playback device. Ham stations route the default device into a USB radio codec, so
    // System.Media (which always uses the default) would send alert tones over the air; this sends
    // them to the picked device (e.g. the speakers) instead. Uses the classic winmm waveOut API so
    // no extra library/DLL is needed. Device 0xFFFFFFFF (WAVE_MAPPER) is "system default".
    public static class WaveOutPlayer
    {
        const uint WAVE_MAPPER = 0xFFFFFFFF;
        const uint WHDR_DONE = 0x00000001;
        const int MAXPNAMELEN = 32;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WAVEOUTCAPS
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAXPNAMELEN)]
            public string szPname;
            public uint dwFormats;
            public ushort wChannels;
            public ushort wReserved1;
            public uint dwSupport;
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

        [DllImport("winmm.dll")] static extern uint waveOutGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern uint waveOutGetDevCaps(IntPtr deviceID, ref WAVEOUTCAPS caps, uint cbSize);
        [DllImport("winmm.dll")] static extern uint waveOutOpen(out IntPtr hWaveOut, uint deviceID, WAVEFORMATEX fmt, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] static extern uint waveOutPrepareHeader(IntPtr hWaveOut, ref WAVEHDR hdr, uint cbSize);
        [DllImport("winmm.dll")] static extern uint waveOutWrite(IntPtr hWaveOut, ref WAVEHDR hdr, uint cbSize);
        [DllImport("winmm.dll")] static extern uint waveOutUnprepareHeader(IntPtr hWaveOut, ref WAVEHDR hdr, uint cbSize);
        [DllImport("winmm.dll")] static extern uint waveOutReset(IntPtr hWaveOut);
        [DllImport("winmm.dll")] static extern uint waveOutClose(IntPtr hWaveOut);

        // Friendly names of the available output devices (index order == waveOut device id). Names are
        // truncated to 31 chars by the API, which is enough to tell "Speakers…" from the codec.
        public static List<string> GetOutputDeviceNames()
        {
            var list = new List<string>();
            try
            {
                uint n = waveOutGetNumDevs();
                for (uint i = 0; i < n; i++)
                {
                    var caps = new WAVEOUTCAPS();
                    if (waveOutGetDevCaps((IntPtr)i, ref caps, (uint)Marshal.SizeOf(typeof(WAVEOUTCAPS))) == 0
                        && !string.IsNullOrWhiteSpace(caps.szPname))
                        list.Add(caps.szPname);
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            return list;
        }

        // The waveOut device id for a saved device name, or WAVE_MAPPER (system default) when the name
        // is empty or no longer present (device unplugged) — so the user is never left silent.
        public static uint ResolveDeviceId(string deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName)) return WAVE_MAPPER;
            try
            {
                uint n = waveOutGetNumDevs();
                for (uint i = 0; i < n; i++)
                {
                    var caps = new WAVEOUTCAPS();
                    if (waveOutGetDevCaps((IntPtr)i, ref caps, (uint)Marshal.SizeOf(typeof(WAVEOUTCAPS))) == 0
                        && string.Equals(caps.szPname, deviceName, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            return WAVE_MAPPER;
        }

        // Plays a PCM WAV file to the given device on a background thread (never blocks the UI).
        public static void Play(string wavPath, uint deviceId)
        {
            Task.Run(() =>
            {
                try { PlayBlocking(wavPath, deviceId); }
                catch (Exception swallowed) { Log.Swallow(swallowed); }
            });
        }

        static void PlayBlocking(string wavPath, uint deviceId)
        {
            if (string.IsNullOrWhiteSpace(wavPath) || !File.Exists(wavPath)) return;

            if (!TryParseWav(File.ReadAllBytes(wavPath), out WAVEFORMATEX fmt, out byte[] data) || data.Length == 0)
                return;

            IntPtr hwo = IntPtr.Zero;
            GCHandle dataHandle = default;
            var hdr = new WAVEHDR();
            bool prepared = false;
            try
            {
                if (waveOutOpen(out hwo, deviceId, fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0 || hwo == IntPtr.Zero)
                    return;

                dataHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
                hdr.lpData = dataHandle.AddrOfPinnedObject();
                hdr.dwBufferLength = (uint)data.Length;

                if (waveOutPrepareHeader(hwo, ref hdr, (uint)Marshal.SizeOf(typeof(WAVEHDR))) != 0) return;
                prepared = true;
                if (waveOutWrite(hwo, ref hdr, (uint)Marshal.SizeOf(typeof(WAVEHDR))) != 0) return;

                // Wait for playback to finish (WHDR_DONE), capped so a stuck device can't hang forever.
                int waited = 0;
                while ((hdr.dwFlags & WHDR_DONE) == 0 && waited < 15000)
                {
                    System.Threading.Thread.Sleep(20);
                    waited += 20;
                }
            }
            finally
            {
                if (hwo != IntPtr.Zero)
                {
                    try { waveOutReset(hwo); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                    if (prepared) try { waveOutUnprepareHeader(hwo, ref hdr, (uint)Marshal.SizeOf(typeof(WAVEHDR))); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                    try { waveOutClose(hwo); } catch (Exception swallowed) { Log.Swallow(swallowed); }
                }
                if (dataHandle.IsAllocated) dataHandle.Free();
            }
        }

        // Minimal RIFF/WAVE parser: pulls the "fmt " chunk into a WAVEFORMATEX and the "data" chunk's
        // PCM bytes. Windows\Media alert files are standard PCM, which is all waveOut needs.
        static bool TryParseWav(byte[] bytes, out WAVEFORMATEX fmt, out byte[] data)
        {
            fmt = null; data = null;
            try
            {
                if (bytes.Length < 12 ||
                    bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F' ||
                    bytes[8] != 'W' || bytes[9] != 'A' || bytes[10] != 'V' || bytes[11] != 'E')
                    return false;

                int pos = 12;
                while (pos + 8 <= bytes.Length)
                {
                    string id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
                    int size = BitConverter.ToInt32(bytes, pos + 4);
                    int body = pos + 8;
                    if (size < 0 || body + size > bytes.Length) break;

                    if (id == "fmt ")
                    {
                        fmt = new WAVEFORMATEX
                        {
                            wFormatTag = BitConverter.ToUInt16(bytes, body + 0),
                            nChannels = BitConverter.ToUInt16(bytes, body + 2),
                            nSamplesPerSec = BitConverter.ToUInt32(bytes, body + 4),
                            nAvgBytesPerSec = BitConverter.ToUInt32(bytes, body + 8),
                            nBlockAlign = BitConverter.ToUInt16(bytes, body + 12),
                            wBitsPerSample = BitConverter.ToUInt16(bytes, body + 14),
                            cbSize = 0
                        };
                    }
                    else if (id == "data")
                    {
                        data = new byte[size];
                        Array.Copy(bytes, body, data, 0, size);
                    }

                    pos = body + size + (size & 1);   // chunks are word-aligned
                }
                return fmt != null && data != null;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return false; }
        }
    }
}
