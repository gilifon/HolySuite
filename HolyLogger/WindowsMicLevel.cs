using System;
using System.Runtime.InteropServices;

namespace HolyLogger
{
    // ── THE MICROPHONE LEVEL WINDOWS ITSELF KEEPS (his request, 2026-11-25) ─────────────────────────
    //
    // The Audio Messages window had a "Volume" slider of its own that multiplied the sound after it
    // arrived, beside a button to Windows' Sound window with the real level in it - two sliders for one
    // job. He chose one: the window's slider now moves Windows' own level, the same number Windows shows
    // under Microphone Properties > Levels.
    //
    // HOW THE TWO NAMES MEET. The recorder knows a device by its waveIn name, which Windows cuts to 31
    // characters ("Microphone (Realtek High Defini"); Core Audio knows the full name ("Microphone
    // (Realtek High Definition Audio)"). The full name starting with the short one is the match. Empty
    // name = the default recording device, as for the recorder.
    internal sealed class WindowsMicLevel : IDisposable
    {
        IAudioEndpointVolume _volume;

        /// <summary>Full name of the device, as Windows shows it.</summary>
        public string Name { get; private set; }

        WindowsMicLevel() { }

        /// <summary>The level control for a recording device, or null when Windows will not give one.</summary>
        public static WindowsMicLevel Open(string waveInName)
        {
            IMMDeviceEnumerator enumerator = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                IMMDevice device = null;
                string name = null;

                if (string.IsNullOrWhiteSpace(waveInName))
                {
                    if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Capture, ERole.Console, out device) != 0) device = null;
                    if (device != null) name = FriendlyName(device);
                }
                else
                {
                    IMMDeviceCollection all;
                    if (enumerator.EnumAudioEndpoints(EDataFlow.Capture, DeviceStateActive, out all) == 0 && all != null)
                    {
                        int count;
                        all.GetCount(out count);
                        string wanted = waveInName.Trim();
                        for (int i = 0; i < count && device == null; i++)
                        {
                            IMMDevice d;
                            if (all.Item(i, out d) != 0 || d == null) continue;
                            string n = FriendlyName(d) ?? string.Empty;
                            if (n.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)) { device = d; name = n; }
                            else Marshal.ReleaseComObject(d);
                        }
                        Marshal.ReleaseComObject(all);
                    }
                }
                if (device == null) return null;

                Guid iid = typeof(IAudioEndpointVolume).GUID;
                object o;
                int hr = device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out o);
                Marshal.ReleaseComObject(device);
                if (hr != 0 || o == null) return null;

                return new WindowsMicLevel { _volume = (IAudioEndpointVolume)o, Name = name };
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return null; }
            finally
            {
                if (enumerator != null) try { Marshal.ReleaseComObject(enumerator); } catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
        }

        /// <summary>The level now, 0..1, or -1 when it cannot be read.</summary>
        public double Get()
        {
            try
            {
                float v;
                if (_volume != null && _volume.GetMasterVolumeLevelScalar(out v) == 0) return v;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); }
            return -1;
        }

        public bool Set(double level)
        {
            try
            {
                if (_volume == null) return false;
                Guid context = Guid.Empty;
                return _volume.SetMasterVolumeLevelScalar((float)Math.Max(0, Math.Min(1, level)), ref context) == 0;
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return false; }
        }

        public void Dispose()
        {
            var v = _volume;
            _volume = null;
            if (v != null) try { Marshal.ReleaseComObject(v); } catch (Exception swallowed) { Log.Swallow(swallowed); }
        }

        static string FriendlyName(IMMDevice device)
        {
            IPropertyStore store = null;
            try
            {
                if (device.OpenPropertyStore(StgmRead, out store) != 0 || store == null) return null;
                var key = new PropertyKey { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
                PropVariant value;
                if (store.GetValue(ref key, out value) != 0) return null;
                try { return value.vt == VtLpwstr ? Marshal.PtrToStringUni(value.pointer) : null; }
                finally { PropVariantClear(ref value); }
            }
            catch (Exception swallowed) { Log.Swallow(swallowed); return null; }
            finally
            {
                if (store != null) try { Marshal.ReleaseComObject(store); } catch (Exception swallowed) { Log.Swallow(swallowed); }
            }
        }

        // ── CORE AUDIO, AS WINDOWS DECLARES IT (mmdeviceapi.h, endpointvolume.h) ──────────────────

        const int DeviceStateActive = 1;
        const int ClsCtxAll = 23;
        const int StgmRead = 0;
        const ushort VtLpwstr = 31;

        enum EDataFlow { Render = 0, Capture = 1 }
        enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

        [StructLayout(LayoutKind.Sequential)]
        struct PropertyKey { public Guid fmtid; public int pid; }

        // 16 bytes in a 32-bit process, 24 in a 64-bit one: the pointer sits after the 8-byte header
        // either way, and the size covers the larger.
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        struct PropVariant
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public IntPtr pointer;
        }

        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant pvar);

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumeratorComObject { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int Item(int index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore properties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetAt(int index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int Commit();
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int GetChannelCount(out uint count);
            [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
            [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        }
    }
}
