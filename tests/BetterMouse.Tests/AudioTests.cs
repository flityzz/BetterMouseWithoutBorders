using System;
using System.IO;
using System.Security.Cryptography;
using Xunit;

namespace BetterMouse.Tests
{
    public class AudioTests
    {
        static readonly DeviceFormat Float48Stereo = DeviceFormat.Create(48000, 2, 4, true);

        static byte[] FloatFrames(int frames, int channels, Func<int, int, float> sample)
        {
            var b = new byte[frames * channels * 4];
            for (int f = 0; f < frames; f++)
                for (int c = 0; c < channels; c++)
                    AudioDsp.WriteSample(b, (f * channels + c) * 4, SampleType.Float32, sample(f, c));
            return b;
        }

        static byte[] Pcm(int frames, int channels, Func<int, short> sample)
        {
            var b = new byte[frames * channels * 2];
            for (int i = 0; i < frames * channels; i++)
            {
                short s = sample(i);
                b[i * 2] = (byte)s;
                b[i * 2 + 1] = (byte)(s >> 8);
            }
            return b;
        }

        [Fact]
        public void AudioDataMessageCarriesKindChannelsAndSamples()
        {
            var pcm = new short[] { 0, 1, -1, short.MaxValue, short.MinValue, 1234 };
            var m = Msg.AudioData(AudioKind.Mic, 1, pcm);
            Assert.Equal((byte)MsgType.AudioData, m[0]);
            Assert.Equal((byte)AudioKind.Mic, m[1]);
            Assert.Equal(1, m[2]);
            Assert.Equal(3 + pcm.Length * 2, m.Length);
            for (int i = 0; i < pcm.Length; i++)
                Assert.Equal(pcm[i], (short)(m[3 + i * 2] | m[4 + i * 2] << 8));

            var want = Msg.AudioWant(AudioKind.System, true);
            Assert.Equal(new byte[] { (byte)MsgType.AudioWant, (byte)AudioKind.System, 1 }, want);
        }

        [Fact]
        public void SampleFormatsRoundTrip()
        {
            var b = new byte[4];
            foreach (var type in new[] { SampleType.Float32, SampleType.Int16, SampleType.Int24, SampleType.Int32 })
                foreach (var v in new[] { 0f, 0.5f, -0.5f, 0.999f, -1f })
                {
                    AudioDsp.WriteSample(b, 0, type, v);
                    Assert.True(Math.Abs(AudioDsp.ReadSample(b, 0, type) - v) < 0.0002f, $"{type} {v}");
                }
            AudioDsp.WriteSample(b, 0, SampleType.Int16, 3f); // clipped, not wrapped
            Assert.True(AudioDsp.ReadSample(b, 0, SampleType.Int16) > 0.99f);
        }

        [Fact]
        public void SameRateCapturePassesSamplesThroughInPackets()
        {
            const int frames = 4800; // 100 ms
            var data = FloatFrames(frames, 2, (n, c) => (c == 0 ? 1 : -1) * (n % 1000) / 2000f);
            var conv = new CaptureConverter(Float48Stereo, 2);
            var packets = new System.Collections.Generic.List<short[]>();
            conv.Push(data, frames, false, p => packets.Add(p));

            Assert.Equal(9, packets.Count); // one frame of look-ahead: 4799 out, 9 full packets
            int f = 0;
            foreach (var p in packets)
            {
                Assert.Equal(AudioWire.FramesPerPacket * 2, p.Length);
                for (int i = 0; i < AudioWire.FramesPerPacket; i++, f++)
                {
                    Assert.Equal(AudioDsp.ToS16((f % 1000) / 2000f), p[i * 2]);
                    Assert.Equal(AudioDsp.ToS16(-(f % 1000) / 2000f), p[i * 2 + 1]);
                }
            }
        }

        [Fact]
        public void CaptureResamples44100To48000()
        {
            var fmt = DeviceFormat.Create(44100, 2, 4, true);
            var conv = new CaptureConverter(fmt, 2);
            int outFrames = 0;
            short peak = 0;
            // 1 s of a 440 Hz tone, fed in 10 ms device packets.
            for (int start = 0; start < 44100; start += 441)
            {
                int s0 = start;
                var data = FloatFrames(441, 2, (f, c) => 0.5f * (float)Math.Sin(2 * Math.PI * 440 * (s0 + f) / 44100.0));
                conv.Push(data, 441, false, p =>
                {
                    outFrames += p.Length / 2;
                    foreach (var s in p) if (s > peak) peak = s;
                });
            }
            Assert.InRange(outFrames, 47520, 48000); // ~1 s at 48 kHz, minus the last partial packet
            Assert.InRange(peak, 16000, 16500);       // amplitude preserved (0.5 → ~16384)
        }

        [Fact]
        public void SilentPacketsAreFlaggedAsSilence()
        {
            var conv = new CaptureConverter(Float48Stereo, 2);
            short[] last = null;
            conv.Push(new byte[0], 1000, true, p => last = p);
            Assert.NotNull(last);
            Assert.True(AudioDsp.IsSilent(last));
            Assert.False(AudioDsp.IsSilent(new short[] { 0, 0, 300, 0 }));
        }

        [Fact]
        public void MixDownFoldsSurroundIntoStereoAndStereoIntoMono()
        {
            var fmt = DeviceFormat.Create(48000, 6, 2, false); // 5.1: FL FR FC LFE BL BR
            var frame = new byte[12];
            float[] values = { 0.1f, 0.2f, 0.4f, 0.9f, 0.2f, 0.4f };
            for (int c = 0; c < 6; c++) AudioDsp.WriteSample(frame, c * 2, SampleType.Int16, values[c]);
            var stereo = new float[2];
            AudioDsp.MixDown(frame, 0, fmt, stereo, 2);
            Assert.True(Math.Abs(stereo[0] - (0.1f + 0.4f * 0.7071f + 0.1f)) < 0.001f); // LFE ignored
            Assert.True(Math.Abs(stereo[1] - (0.2f + 0.4f * 0.7071f + 0.2f)) < 0.001f);

            var mono = new float[1];
            AudioDsp.MixDown(frame, 0, fmt, mono, 1);
            Assert.True(Math.Abs(mono[0] - 0.15f) < 0.001f);
        }

        [Fact]
        public void PlaybackWaitsForTargetLevelThenPlaysInOrder()
        {
            var buf = new PlaybackBuffer(targetMs: 40);
            var outBuf = new float[480 * 2];
            buf.Write(Pcm(1440, 2, i => (short)(i / 2)), 0, 1440, 2); // 30 ms
            Assert.False(buf.Read(outBuf, 480, 2, 48000));           // still buffering
            Assert.Equal(0f, outBuf[0]);

            buf.Write(Pcm(960, 2, i => (short)(1440 + i / 2)), 0, 960, 2); // now 50 ms
            Assert.True(buf.Read(outBuf, 480, 2, 48000));
            for (int f = 0; f < 480; f++)
            {
                Assert.True(Math.Abs(outBuf[f * 2] - f / 32768f) < 1e-4f, "frame " + f);
                Assert.Equal(outBuf[f * 2], outBuf[f * 2 + 1]);
            }
        }

        [Fact]
        public void PlaybackRebuffersAfterRunningDry()
        {
            var buf = new PlaybackBuffer(targetMs: 40);
            var outBuf = new float[4800 * 2];
            buf.Write(Pcm(2400, 1, i => 1000), 0, 2400, 1); // 50 ms mono
            Assert.True(buf.Read(outBuf, 4800, 2, 48000));  // asks for 100 ms: plays 50, then silence
            Assert.True(buf.IsBuffering);
            Assert.Equal(0f, outBuf[4000 * 2]);
            Assert.Equal(1000 / 32768f, outBuf[0], 5);
            buf.Write(Pcm(480, 1, i => 1000), 0, 480, 1);
            Assert.False(buf.Read(outBuf, 480, 2, 48000)); // below target again: silent, waiting
        }

        [Fact]
        public void PlaybackDriftCorrectionTracksTheBufferLevel()
        {
            var full = new PlaybackBuffer(targetMs: 40);
            var outBuf = new float[480 * 2];
            full.Write(Pcm(9600, 2, i => 0), 0, 9600, 2); // 200 ms, far above target
            for (int i = 0; i < 50; i++)
            {
                full.Write(Pcm(480, 2, k => 0), 0, 480, 2);
                full.Read(outBuf, 480, 2, 48000);
            }
            Assert.True(full.Correction > 0, "plays faster when too much is buffered");
            Assert.True(full.Correction <= 0.005);

            // Feeding at exactly the playback rate around the target keeps the speed near 1.
            var steady = new PlaybackBuffer(targetMs: 40);
            steady.Write(Pcm(1440, 2, i => 0), 0, 1440, 2); // + 480 per round = target at each read
            for (int i = 0; i < 200; i++)
            {
                steady.Write(Pcm(480, 2, k => 0), 0, 480, 2);
                steady.Read(outBuf, 480, 2, 48000);
            }
            Assert.True(Math.Abs(steady.Correction) < 0.002, "correction " + steady.Correction);
        }

        [Fact]
        public void PlaybackJumpsBackToLiveWhenFarBehind()
        {
            var buf = new PlaybackBuffer(targetMs: 40, maxMs: 250);
            buf.Write(Pcm(48000, 2, i => 0), 0, 48000, 2); // 1 s arrives at once (after a stall)
            Assert.InRange(buf.LevelFrames, 1920, 1920);
        }

        [Fact]
        public void PlaybackConvertsToDeviceRateAndChannels()
        {
            var buf = new PlaybackBuffer(targetMs: 40);
            buf.Write(Pcm(4800, 2, i => (short)(i % 2 == 0 ? 8000 : -8000)), 0, 4800, 2);
            var outBuf = new float[441 * 6];
            Assert.True(buf.Read(outBuf, 441, 6, 44100)); // 10 ms on a 44.1 kHz 5.1 device
            Assert.Equal(8000 / 32768f, outBuf[0], 4);
            Assert.Equal(-8000 / 32768f, outBuf[1], 4);
            Assert.Equal(0f, outBuf[2]);
            Assert.InRange(4800 - buf.LevelFrames, 479, 484); // 441 frames at 44.1 kHz ≈ 480 at 48 kHz
        }

        [Fact]
        public void AudioSettingsAreSavedAndLoaded()
        {
            var path = Path.Combine(Path.GetTempPath(), "bm-audio-" + Guid.NewGuid().ToString("N") + ".ini");
            try
            {
                var s = new Settings
                {
                    SendAudio = true, MuteWhileSending = false, PlayAudio = true, AudioOutputDevice = "Headset",
                    SendMic = true, MicDevice = "USB Mic", ReceiveMic = true, MicOutputDevice = "CABLE-A Input",
                };
                s.SaveTo(path, DataProtectionScope.CurrentUser);
                var r = Settings.LoadFrom(path, DataProtectionScope.CurrentUser);
                Assert.True(r.SendAudio);
                Assert.False(r.MuteWhileSending);
                Assert.True(r.PlayAudio);
                Assert.Equal("Headset", r.AudioOutputDevice);
                Assert.True(r.SendMic);
                Assert.Equal("USB Mic", r.MicDevice);
                Assert.True(r.ReceiveMic);
                Assert.Equal("CABLE-A Input", r.MicOutputDevice);

                var fresh = new Settings();
                Assert.False(fresh.SendAudio || fresh.PlayAudio || fresh.SendMic || fresh.ReceiveMic); // off until chosen
                Assert.Equal(AudioShare.VbCableInput, fresh.MicOutputDevice);
                fresh.CopyAudioFrom(r);
                Assert.Equal("USB Mic", fresh.MicDevice);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void AudioShareStreamsOnlyWhatThePeerWants()
        {
            var settings = new Settings();
            var control = new System.Collections.Generic.List<byte[]>();
            var share = new AudioShare(() => settings, m => { }, m => { lock (control) control.Add(m); });

            share.OnConnected();
            Assert.Equal(2, control.Count); // tells the other PC what it wants (nothing yet)
            Assert.All(control, m => Assert.Equal(0, m[2]));

            control.Clear();
            settings.PlayAudio = true;
            share.SettingsChanged();
            Assert.Contains(control, m => m[0] == (byte)MsgType.AudioWant && m[1] == (byte)AudioKind.System && m[2] == 1);

            // Audio arriving for a stream this PC didn't ask for is ignored (no player is started).
            share.Receive(Msg.AudioData(AudioKind.Mic, 1, new short[480]));
            Assert.Equal("Audio: nothing streaming", share.Describe());

            share.OnDisconnected();
            control.Clear();
            settings.ReceiveMic = true;
            share.SettingsChanged();
            Assert.Empty(control); // nothing is sent while disconnected
            share.Dispose();
        }
    }
}
