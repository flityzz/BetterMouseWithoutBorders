using System;
using System.Collections.Generic;
using System.Threading;

namespace BetterMouse
{
    /// <summary>
    /// Sound and microphone sharing between the two PCs.
    ///
    /// Each PC says which streams it wants to RECEIVE (<see cref="MsgType.AudioWant"/>); a stream
    /// only flows while the receiver wants it and the sender's own setting allows it. Typical setup:
    /// - Client: "Send this PC's sound" + "Receive the other PC's microphone" (into VB-Cable).
    /// - Host:   "Play the other PC's sound here" + "Send my microphone".
    /// Audio rides the existing encrypted connection in its own queue, behind mouse/keyboard input
    /// but ahead of clipboard transfers.
    /// </summary>
    internal sealed class AudioShare : IDisposable
    {
        public const string VbCableInput = "CABLE Input";
        public const string VbCableOutput = "CABLE Output";
        public const string VbCableUrl = "https://vb-audio.com/Cable/";

        readonly Func<Settings> settings;
        readonly Action<byte[]> sendMedia;
        readonly Action<byte[]> sendControl;
        readonly object gate = new object();
        bool connected, disposed;
        bool peerWantsSystem, peerWantsMic;
        AudioSender systemSender, micSender;
        AudioPlayer systemPlayer, micPlayer;

        public AudioShare(Func<Settings> settings, Action<byte[]> sendMedia, Action<byte[]> sendControl)
        {
            this.settings = settings;
            this.sendMedia = sendMedia;
            this.sendControl = sendControl;
        }

        public void OnConnected()
        {
            lock (gate)
            {
                connected = true;
                peerWantsSystem = peerWantsMic = false;
            }
            SendWants();
            Update();
        }

        public void OnDisconnected()
        {
            lock (gate)
            {
                connected = false;
                peerWantsSystem = peerWantsMic = false;
            }
            Update();
        }

        /// <summary>Call after any audio setting changed.</summary>
        public void SettingsChanged()
        {
            SendWants();
            Update();
        }

        void SendWants()
        {
            lock (gate) if (!connected) return;
            var s = settings();
            sendControl(Msg.AudioWant(AudioKind.System, s.PlayAudio));
            sendControl(Msg.AudioWant(AudioKind.Mic, s.ReceiveMic));
        }

        /// <summary>Network reader thread: AudioWant and AudioData messages.</summary>
        public void Receive(byte[] m)
        {
            if (m.Length < 3) return;
            var kind = (AudioKind)m[1];
            if (kind != AudioKind.System && kind != AudioKind.Mic) return;

            if ((MsgType)m[0] == MsgType.AudioWant)
            {
                bool want = m[2] != 0;
                lock (gate)
                {
                    if (kind == AudioKind.System) peerWantsSystem = want;
                    else peerWantsMic = want;
                }
                Log.Info($"The other PC {(want ? "wants" : "no longer wants")} this PC's {(kind == AudioKind.Mic ? "microphone" : "sound")}");
                Update();
                return;
            }

            if ((MsgType)m[0] != MsgType.AudioData) return;
            int ch = m[2];
            if (ch < 1 || ch > 2) return;
            int frames = (m.Length - 3) / (2 * ch);
            AudioPlayer player;
            lock (gate)
            {
                if (!connected || disposed) return;
                var s = settings();
                if (kind == AudioKind.System)
                {
                    if (!s.PlayAudio) return;
                    player = systemPlayer ?? (systemPlayer = new AudioPlayer(kind, s.AudioOutputDevice));
                }
                else
                {
                    if (!s.ReceiveMic) return;
                    player = micPlayer ?? (micPlayer = new AudioPlayer(kind, s.MicOutputDevice));
                }
            }
            player.Write(m, 3, frames, ch);
        }

        /// <summary>Starts/stops capture and playback to match the settings and what the other PC wants.</summary>
        void Update()
        {
            var stop = new List<IDisposable>();
            lock (gate)
            {
                var s = settings();
                bool live = connected && !disposed;

                bool sendSystem = live && s.SendAudio && peerWantsSystem;
                if (systemSender != null && (!sendSystem || systemSender.Config != AudioSender.ConfigFor(AudioKind.System, s)))
                {
                    stop.Add(systemSender);
                    systemSender = null;
                }
                if (sendSystem && systemSender == null) systemSender = new AudioSender(AudioKind.System, s, sendMedia);

                bool sendMic = live && s.SendMic && peerWantsMic;
                if (micSender != null && (!sendMic || micSender.Config != AudioSender.ConfigFor(AudioKind.Mic, s)))
                {
                    stop.Add(micSender);
                    micSender = null;
                }
                if (sendMic && micSender == null) micSender = new AudioSender(AudioKind.Mic, s, sendMedia);

                // Players are created when audio arrives; here they are only retired.
                if (systemPlayer != null && (!live || !s.PlayAudio || systemPlayer.DeviceName != s.AudioOutputDevice))
                {
                    stop.Add(systemPlayer);
                    systemPlayer = null;
                }
                if (micPlayer != null && (!live || !s.ReceiveMic || micPlayer.DeviceName != s.MicOutputDevice))
                {
                    stop.Add(micPlayer);
                    micPlayer = null;
                }
            }
            // Outside the lock: stopping waits for the audio thread (which restores the speakers' mute).
            foreach (var d in stop) d.Dispose();
        }

        /// <summary>One line for the tray menu.</summary>
        public string Describe()
        {
            var parts = new List<string>();
            lock (gate)
            {
                if (systemSender != null) parts.Add(systemSender.Problem ?? "sending this PC's sound");
                if (micSender != null) parts.Add(micSender.Problem ?? "sending my microphone");
                if (systemPlayer != null && systemPlayer.Busy) parts.Add(systemPlayer.Problem ?? "playing the other PC's sound");
                if (micPlayer != null && micPlayer.Busy) parts.Add(micPlayer.Problem ?? $"other PC's mic → {micPlayer.DeviceName}");
            }
            return parts.Count == 0 ? "Audio: nothing streaming" : "Audio: " + string.Join(" · ", parts);
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
            }
            Update();
        }
    }

    /// <summary>Captures one stream (speakers via loopback, or the microphone) and sends it.</summary>
    internal sealed class AudioSender : IDisposable
    {
        readonly AudioKind kind;
        readonly string deviceName;
        readonly bool muteSpeakers;
        readonly string cableName;
        readonly Action<byte[]> send;
        readonly Thread thread;
        volatile bool stopping;
        volatile string problem;

        public string Config { get; }
        public string Problem => problem;

        public static string ConfigFor(AudioKind kind, Settings s) =>
            kind == AudioKind.Mic ? "mic|" + s.MicDevice : "sys|" + s.MuteWhileSending;

        public AudioSender(AudioKind kind, Settings s, Action<byte[]> send)
        {
            this.kind = kind;
            this.send = send;
            deviceName = kind == AudioKind.Mic ? s.MicDevice : "";
            muteSpeakers = kind == AudioKind.System && s.MuteWhileSending;
            cableName = s.MicOutputDevice;
            Config = ConfigFor(kind, s);
            thread = new Thread(Run) { IsBackground = true, Name = "BetterMouse audio capture (" + kind + ")", Priority = ThreadPriority.AboveNormal };
            thread.Start();
        }

        bool Loopback => kind == AudioKind.System;

        void Run()
        {
            Log.Info(Loopback ? "Sending this PC's sound to the other PC" : "Sending this PC's microphone to the other PC");
            while (!stopping)
            {
                try
                {
                    Capture();
                }
                catch (Exception ex)
                {
                    problem = (Loopback ? "sound" : "microphone") + " capture failed: " + ex.Message;
                    Log.Warn($"Audio capture ({kind}) failed, retrying: {ex.Message}");
                    Sleep(2000);
                }
            }
            Log.Info(Loopback ? "Stopped sending this PC's sound" : "Stopped sending this PC's microphone");
        }

        void Capture()
        {
            var enumerator = Wasapi.CreateEnumerator();
            IMMDevice device = null;
            EndpointMute mute = null;
            WasapiStream stream = null;
            try
            {
                var flow = Loopback ? EDataFlow.Render : EDataFlow.Capture;
                device = Wasapi.FindDevice(enumerator, flow, deviceName);
                if (device == null)
                {
                    problem = string.IsNullOrWhiteSpace(deviceName)
                        ? (Loopback ? "no speakers/headphones found" : "no microphone found")
                        : $"microphone '{deviceName}' not found";
                    Sleep(3000);
                    return;
                }
                var name = Wasapi.GetName(device);
                var id = Wasapi.GetId(device);
                if (Loopback && !string.IsNullOrWhiteSpace(cableName) && name.IndexOf(cableName, StringComparison.OrdinalIgnoreCase) >= 0)
                    Log.Warn($"The default playback device is '{name}' (the virtual cable). Set your real speakers as default, " +
                             "or the other PC's microphone will be sent back to it.");

                stream = WasapiStream.Open(device, isCapture: true, loopback: Loopback, eventDriven: !Loopback);
                if (Loopback && muteSpeakers) mute = EndpointMute.Apply(device);
                Log.Info($"Capturing {(Loopback ? "sound playing on" : "microphone")} '{name}' ({stream.Format})");
                problem = null;

                int outChannels = AudioWire.Channels(kind);
                var converter = new CaptureConverter(stream.Format, outChannels);
                Action<short[]> emit = pcm =>
                {
                    // Nothing playing: send nothing (the other PC fills the gap with silence).
                    if (Loopback && AudioDsp.IsSilent(pcm)) return;
                    send(Msg.AudioData(kind, outChannels, pcm));
                };
                Action<byte[], int, bool> onData = (data, frames, silent) => converter.Push(data, frames, silent, emit);

                stream.Start();
                int lastDeviceCheck = Environment.TickCount;
                while (!stopping)
                {
                    stream.Wait(Loopback ? 10 : 200);
                    stream.ReadAll(onData);
                    if (string.IsNullOrWhiteSpace(deviceName) && unchecked(Environment.TickCount - lastDeviceCheck) > 2000)
                    {
                        lastDeviceCheck = Environment.TickCount;
                        if (Wasapi.DefaultId(enumerator, flow) != id)
                        {
                            Log.Info("Default audio device changed: switching");
                            return;
                        }
                    }
                }
            }
            finally
            {
                stream?.Dispose();
                mute?.Dispose(); // speakers back to how they were
                Wasapi.Release(device);
                Wasapi.Release(enumerator);
            }
        }

        void Sleep(int ms)
        {
            for (int waited = 0; waited < ms && !stopping; waited += 100) Thread.Sleep(100);
        }

        public void Dispose()
        {
            stopping = true;
            if (Thread.CurrentThread != thread) thread.Join(3000);
        }
    }

    /// <summary>
    /// Plays one received stream on a local output device. It only holds the device while audio
    /// is arriving, so an idle stream never keeps the PC awake.
    /// </summary>
    internal sealed class AudioPlayer : IDisposable
    {
        const int IdleMs = 3000;
        const int DeviceFillMs = 30;

        readonly AudioKind kind;
        readonly PlaybackBuffer buffer = new PlaybackBuffer();
        readonly AutoResetEvent dataArrived = new AutoResetEvent(false);
        readonly Thread thread;
        volatile bool stopping;
        volatile string problem;
        bool loggedMissing;

        /// <summary>Output device name to look for ("" = default speakers).</summary>
        public string DeviceName { get; }
        public string Problem => problem;
        public bool Busy => buffer.HasRecentData(IdleMs);

        public AudioPlayer(AudioKind kind, string deviceName)
        {
            this.kind = kind;
            DeviceName = deviceName ?? "";
            thread = new Thread(Run) { IsBackground = true, Name = "BetterMouse audio playback (" + kind + ")", Priority = ThreadPriority.AboveNormal };
            thread.Start();
        }

        public void Write(byte[] m, int offset, int frames, int channels)
        {
            buffer.Write(m, offset, frames, channels);
            dataArrived.Set();
        }

        void Run()
        {
            while (!stopping)
            {
                if (!buffer.HasRecentData(IdleMs))
                {
                    dataArrived.WaitOne(500);
                    continue;
                }
                try
                {
                    PlayUntilIdle();
                }
                catch (Exception ex)
                {
                    problem = "playback failed: " + ex.Message;
                    Log.Warn($"Audio playback ({kind}) failed, retrying: {ex.Message}");
                    Sleep(1000);
                }
            }
        }

        void PlayUntilIdle()
        {
            var enumerator = Wasapi.CreateEnumerator();
            IMMDevice device = null;
            WasapiStream stream = null;
            try
            {
                device = Wasapi.FindDevice(enumerator, EDataFlow.Render, DeviceName);
                if (device == null)
                {
                    problem = DeviceName.Length > 0
                        ? $"'{DeviceName}' not found – install VB-Audio Virtual Cable"
                        : "no speakers/headphones found";
                    if (!loggedMissing)
                    {
                        loggedMissing = true;
                        Log.Warn($"Audio output '{DeviceName}' not found. Playback devices on this PC: " +
                                 string.Join(", ", Wasapi.ListNames(EDataFlow.Render)));
                    }
                    Sleep(3000);
                    buffer.Clear();
                    return;
                }
                loggedMissing = false;
                var name = Wasapi.GetName(device);
                var id = Wasapi.GetId(device);
                stream = WasapiStream.Open(device, isCapture: false, loopback: false, eventDriven: true);
                var fmt = stream.Format;
                Log.Info($"Playing the other PC's {(kind == AudioKind.Mic ? "microphone" : "sound")} on '{name}' ({fmt})");
                problem = null;

                int fill = Math.Min(fmt.Rate * DeviceFillMs / 1000, stream.BufferFrames);
                var samples = new float[stream.BufferFrames * fmt.Channels];
                stream.Start();
                int lastDeviceCheck = Environment.TickCount;
                while (!stopping)
                {
                    stream.Wait(200);
                    int want = fill - stream.Padding;
                    if (want > 0)
                    {
                        buffer.Read(samples, want, fmt.Channels, fmt.Rate);
                        stream.Write(samples, want);
                    }
                    if (!buffer.HasRecentData(IdleMs)) return; // release the device while nothing arrives
                    if (DeviceName.Length == 0 && unchecked(Environment.TickCount - lastDeviceCheck) > 2000)
                    {
                        lastDeviceCheck = Environment.TickCount;
                        if (Wasapi.DefaultId(enumerator, EDataFlow.Render) != id)
                        {
                            Log.Info("Default audio device changed: switching");
                            return;
                        }
                    }
                }
            }
            finally
            {
                stream?.Dispose();
                Wasapi.Release(device);
                Wasapi.Release(enumerator);
            }
        }

        void Sleep(int ms)
        {
            for (int waited = 0; waited < ms && !stopping; waited += 100) Thread.Sleep(100);
        }

        public void Dispose()
        {
            stopping = true;
            dataArrived.Set();
            if (Thread.CurrentThread != thread) thread.Join(3000);
        }
    }
}
