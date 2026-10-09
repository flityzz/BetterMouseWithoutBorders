using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace BetterMouse
{
    internal enum EDataFlow { Render = 0, Capture = 1 }

    internal enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    /// <summary>
    /// Minimal Windows Core Audio (WASAPI) interop: find endpoints, shared-mode capture, loopback
    /// capture and render, endpoint mute. Everything here works without admin rights.
    /// Every COM object must be created and used on the same (MTA) worker thread.
    /// </summary>
    internal static class Wasapi
    {
        public const int StreamFlagsLoopback = 0x00020000;
        public const int StreamFlagsEventCallback = 0x00040000;
        public const int BufferFlagsSilent = 0x2;
        const int ClsctxAll = 0x17;
        const int DeviceStateActive = 1;
        const ushort VtLpwstr = 31;

        static readonly Guid ClsidMMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        public static readonly Guid IidAudioClient = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
        public static readonly Guid IidAudioCaptureClient = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
        public static readonly Guid IidAudioRenderClient = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
        public static readonly Guid IidAudioEndpointVolume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
        static readonly Guid SubtypeFloat = new Guid("00000003-0000-0010-8000-00AA00389B71");
        static readonly Guid SubtypePcm = new Guid("00000001-0000-0010-8000-00AA00389B71");
        static readonly PropertyKey FriendlyName = new PropertyKey { FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), PropertyId = 14 };

        public static void Check(int hr)
        {
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }

        public static IMMDeviceEnumerator CreateEnumerator() =>
            (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidMMDeviceEnumerator));

        /// <summary>
        /// The first active endpoint whose name contains <paramref name="nameContains"/> (any case),
        /// or the default endpoint when the name is empty. Null if there is none.
        /// </summary>
        public static IMMDevice FindDevice(IMMDeviceEnumerator e, EDataFlow flow, string nameContains)
        {
            if (string.IsNullOrWhiteSpace(nameContains))
                return e.GetDefaultAudioEndpoint(flow, ERole.Console, out var def) >= 0 ? def : null;

            Check(e.EnumAudioEndpoints(flow, DeviceStateActive, out var all));
            try
            {
                Check(all.GetCount(out var n));
                for (uint i = 0; i < n; i++)
                {
                    if (all.Item(i, out var d) < 0) continue;
                    if (GetName(d).IndexOf(nameContains.Trim(), StringComparison.OrdinalIgnoreCase) >= 0) return d;
                    Release(d);
                }
            }
            finally { Release(all); }
            return null;
        }

        /// <summary>Names of the active endpoints (for the log and for "is VB-Cable installed?").</summary>
        public static List<string> ListNames(EDataFlow flow)
        {
            var names = new List<string>();
            var e = CreateEnumerator();
            try
            {
                Check(e.EnumAudioEndpoints(flow, DeviceStateActive, out var all));
                try
                {
                    Check(all.GetCount(out var n));
                    for (uint i = 0; i < n; i++)
                    {
                        if (all.Item(i, out var d) < 0) continue;
                        names.Add(GetName(d));
                        Release(d);
                    }
                }
                finally { Release(all); }
            }
            finally { Release(e); }
            return names;
        }

        public static bool DeviceExists(EDataFlow flow, string nameContains)
        {
            try
            {
                foreach (var n in ListNames(flow))
                    if (n.IndexOf(nameContains.Trim(), StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            catch (Exception ex) { Log.Warn("Could not list audio devices: " + ex.Message); }
            return false;
        }

        /// <summary>The current default endpoint's id, or "" (used to follow headphones being plugged in).</summary>
        public static string DefaultId(IMMDeviceEnumerator e, EDataFlow flow)
        {
            if (e.GetDefaultAudioEndpoint(flow, ERole.Console, out var d) < 0) return "";
            try { return GetId(d); }
            finally { Release(d); }
        }

        public static string GetId(IMMDevice d) => d.GetId(out var id) >= 0 ? id ?? "" : "";

        public static string GetName(IMMDevice d)
        {
            if (d.OpenPropertyStore(0 /* STGM_READ */, out var store) < 0) return "";
            try
            {
                var key = FriendlyName;
                if (store.GetValue(ref key, out var v) < 0) return "";
                try { return v.Type == VtLpwstr && v.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(v.Pointer) : ""; }
                finally { PropVariantClear(ref v); }
            }
            finally { Release(store); }
        }

        public static object Activate(IMMDevice d, Guid iid)
        {
            Check(d.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var o));
            return o;
        }

        /// <summary>Reads a WAVEFORMATEX / WAVEFORMATEXTENSIBLE (shared-mode mix formats only).</summary>
        public static DeviceFormat ParseFormat(IntPtr p)
        {
            int tag = (ushort)Marshal.ReadInt16(p, 0);
            int channels = Marshal.ReadInt16(p, 2);
            int rate = Marshal.ReadInt32(p, 4);
            int blockAlign = Marshal.ReadInt16(p, 12);
            bool isFloat = tag == 3;
            if (tag == 0xFFFE)
            {
                var sub = (Guid)Marshal.PtrToStructure(p + 24, typeof(Guid));
                if (sub == SubtypeFloat) isFloat = true;
                else if (sub != SubtypePcm) throw new NotSupportedException("Unsupported audio format " + sub);
            }
            else if (tag != 1 && tag != 3)
            {
                throw new NotSupportedException("Unsupported audio format tag " + tag);
            }
            if (channels <= 0 || blockAlign <= 0 || blockAlign % channels != 0)
                throw new NotSupportedException("Unsupported audio layout");
            return DeviceFormat.Create(rate, channels, blockAlign / channels, isFloat);
        }

        public static void Release(object o)
        {
            try { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); }
            catch { }
        }

        [DllImport("ole32.dll")]
        static extern int PropVariantClear(ref PropVariant pvar);
    }

    /// <summary>One shared-mode WASAPI stream (capture, loopback capture or render) on one endpoint.</summary>
    internal sealed class WasapiStream : IDisposable
    {
        const long BufferDuration = 1_000_000; // 100 ms, in 100-ns units

        IAudioClient client;
        IAudioCaptureClient capture;
        IAudioRenderClient render;
        AutoResetEvent ready;
        byte[] scratch = new byte[0];

        public DeviceFormat Format { get; private set; }
        public int BufferFrames { get; private set; }

        WasapiStream() { }

        /// <param name="eventDriven">
        /// Wake up on every device period. Loopback streams are polled instead: older Windows
        /// versions never signal the event for loopback capture.
        /// </param>
        public static WasapiStream Open(IMMDevice device, bool isCapture, bool loopback, bool eventDriven)
        {
            var s = new WasapiStream();
            try
            {
                s.client = (IAudioClient)Wasapi.Activate(device, Wasapi.IidAudioClient);
                Wasapi.Check(s.client.GetMixFormat(out var mix));
                try
                {
                    s.Format = Wasapi.ParseFormat(mix);
                    int flags = (loopback ? Wasapi.StreamFlagsLoopback : 0) | (eventDriven ? Wasapi.StreamFlagsEventCallback : 0);
                    Wasapi.Check(s.client.Initialize(0 /* shared */, flags, BufferDuration, 0, mix, IntPtr.Zero));
                }
                finally { Marshal.FreeCoTaskMem(mix); }

                Wasapi.Check(s.client.GetBufferSize(out var frames));
                s.BufferFrames = (int)frames;
                if (eventDriven)
                {
                    s.ready = new AutoResetEvent(false);
                    Wasapi.Check(s.client.SetEventHandle(s.ready.SafeWaitHandle.DangerousGetHandle()));
                }
                var iid = isCapture ? Wasapi.IidAudioCaptureClient : Wasapi.IidAudioRenderClient;
                Wasapi.Check(s.client.GetService(ref iid, out var service));
                if (isCapture) s.capture = (IAudioCaptureClient)service;
                else s.render = (IAudioRenderClient)service;
                return s;
            }
            catch
            {
                s.Dispose();
                throw;
            }
        }

        public void Start() => Wasapi.Check(client.Start());

        /// <summary>Waits for the next device period (or simply sleeps for polled streams).</summary>
        public void Wait(int ms)
        {
            if (ready != null) ready.WaitOne(ms);
            else Thread.Sleep(ms);
        }

        /// <summary>Hands every captured packet to <paramref name="onData"/> (bytes, frames, silent).</summary>
        public void ReadAll(Action<byte[], int, bool> onData)
        {
            while (true)
            {
                Wasapi.Check(capture.GetNextPacketSize(out var next));
                if (next == 0) return;
                Wasapi.Check(capture.GetBuffer(out var data, out var frames, out var flags, out _, out _));
                try
                {
                    int bytes = (int)frames * Format.BlockAlign;
                    if (scratch.Length < bytes) scratch = new byte[bytes];
                    bool silent = (flags & Wasapi.BufferFlagsSilent) != 0;
                    if (!silent && bytes > 0) Marshal.Copy(data, scratch, 0, bytes);
                    onData(scratch, (int)frames, silent);
                }
                finally
                {
                    Wasapi.Check(capture.ReleaseBuffer(frames));
                }
            }
        }

        /// <summary>Frames queued in the device buffer that have not been played yet.</summary>
        public int Padding
        {
            get
            {
                Wasapi.Check(client.GetCurrentPadding(out var p));
                return (int)p;
            }
        }

        /// <summary>Writes interleaved float samples (device channel count) in the device's format.</summary>
        public void Write(float[] samples, int frames)
        {
            if (frames <= 0) return;
            int bytes = frames * Format.BlockAlign;
            if (scratch.Length < bytes) scratch = new byte[bytes];
            AudioDsp.Encode(samples, frames * Format.Channels, Format, scratch);
            Wasapi.Check(render.GetBuffer((uint)frames, out var data));
            Marshal.Copy(scratch, 0, data, bytes);
            Wasapi.Check(render.ReleaseBuffer((uint)frames, 0));
        }

        public void Dispose()
        {
            try { client?.Stop(); } catch { }
            Wasapi.Release(capture);
            Wasapi.Release(render);
            Wasapi.Release(client);
            capture = null;
            render = null;
            client = null;
            ready?.Dispose();
            ready = null;
        }
    }

    /// <summary>Mutes an output device and restores it afterwards (only if we were the ones who muted it).</summary>
    internal sealed class EndpointMute : IDisposable
    {
        IAudioEndpointVolume volume;
        bool mutedByUs;

        public static EndpointMute Apply(IMMDevice device)
        {
            var m = new EndpointMute();
            try
            {
                m.volume = (IAudioEndpointVolume)Wasapi.Activate(device, Wasapi.IidAudioEndpointVolume);
                Wasapi.Check(m.volume.GetMute(out var already));
                if (already == 0)
                {
                    Wasapi.Check(m.volume.SetMute(1, IntPtr.Zero));
                    m.mutedByUs = true;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Could not mute this PC's speakers: " + ex.Message);
            }
            return m;
        }

        public void Dispose()
        {
            try { if (mutedByUs) volume?.SetMute(0, IntPtr.Zero); }
            catch (Exception ex) { Log.Warn("Could not unmute this PC's speakers: " + ex.Message); }
            mutedByUs = false;
            Wasapi.Release(volume);
            volume = null;
        }
    }

    // ------------------------------------------------------------------ COM declarations
    // Methods are declared in vtable order; unused ones are still listed so the slots line up.

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariant
    {
        public ushort Type;
        public ushort Reserved1, Reserved2, Reserved3;
        public IntPtr Pointer;
        public IntPtr Pointer2;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out int flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint frames, int flags);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, IntPtr eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, IntPtr eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, IntPtr eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, IntPtr eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute(int mute, IntPtr eventContext);
        [PreserveSig] int GetMute(out int mute);
    }
}
