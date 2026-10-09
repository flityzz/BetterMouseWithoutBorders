using System;

namespace BetterMouse
{
    /// <summary>Which audio stream a message carries.</summary>
    internal enum AudioKind : byte
    {
        /// <summary>Everything the sending PC plays on its speakers (loopback capture).</summary>
        System = 1,
        /// <summary>The sending PC's microphone.</summary>
        Mic = 2,
    }

    /// <summary>Format on the wire: 16-bit little-endian PCM, 48 kHz, 10 ms per packet.</summary>
    internal static class AudioWire
    {
        public const int SampleRate = 48000;
        public const int FramesPerPacket = 480;
        public static int Channels(AudioKind kind) => kind == AudioKind.Mic ? 1 : 2;
    }

    internal enum SampleType : byte { Float32, Int16, Int24, Int32 }

    /// <summary>A device's shared-mode mix format.</summary>
    internal struct DeviceFormat
    {
        public int Rate;
        public int Channels;
        public int BytesPerSample;
        public SampleType Type;

        public int BlockAlign => Channels * BytesPerSample;

        public static DeviceFormat Create(int rate, int channels, int bytesPerSample, bool isFloat)
        {
            SampleType type;
            if (isFloat && bytesPerSample == 4) type = SampleType.Float32;
            else if (!isFloat && bytesPerSample == 2) type = SampleType.Int16;
            else if (!isFloat && bytesPerSample == 3) type = SampleType.Int24;
            else if (!isFloat && bytesPerSample == 4) type = SampleType.Int32; // also 24-bit in a 32-bit container
            else throw new NotSupportedException($"Unsupported sample size ({bytesPerSample * 8}-bit{(isFloat ? " float" : "")})");
            return new DeviceFormat { Rate = rate, Channels = channels, BytesPerSample = bytesPerSample, Type = type };
        }

        public override string ToString() => $"{Rate} Hz, {Channels} ch, {Type}";
    }

    internal static class AudioDsp
    {
        public static float ReadSample(byte[] b, int off, SampleType type)
        {
            switch (type)
            {
                case SampleType.Float32: return BitConverter.ToSingle(b, off);
                case SampleType.Int16: return (short)(b[off] | b[off + 1] << 8) / 32768f;
                case SampleType.Int24: return ((b[off] << 8 | b[off + 1] << 16 | b[off + 2] << 24) >> 8) / 8388608f;
                default: return BitConverter.ToInt32(b, off) / 2147483648f;
            }
        }

        public static void WriteSample(byte[] b, int off, SampleType type, float v)
        {
            if (v > 1f) v = 1f;
            else if (v < -1f) v = -1f;
            switch (type)
            {
                case SampleType.Float32:
                {
                    var bytes = BitConverter.GetBytes(v);
                    b[off] = bytes[0]; b[off + 1] = bytes[1]; b[off + 2] = bytes[2]; b[off + 3] = bytes[3];
                    break;
                }
                case SampleType.Int16:
                {
                    int s = (int)Math.Round(v * 32767f);
                    b[off] = (byte)s; b[off + 1] = (byte)(s >> 8);
                    break;
                }
                case SampleType.Int24:
                {
                    int s = (int)Math.Round(v * 8388607f);
                    b[off] = (byte)s; b[off + 1] = (byte)(s >> 8); b[off + 2] = (byte)(s >> 16);
                    break;
                }
                default:
                {
                    int s = (int)Math.Round(v * 2147483520.0);
                    b[off] = (byte)s; b[off + 1] = (byte)(s >> 8); b[off + 2] = (byte)(s >> 16); b[off + 3] = (byte)(s >> 24);
                    break;
                }
            }
        }

        /// <summary>Interleaved floats (device channel count) to the device's sample format.</summary>
        public static void Encode(float[] samples, int count, DeviceFormat fmt, byte[] dst)
        {
            int size = fmt.BytesPerSample;
            for (int i = 0; i < count; i++) WriteSample(dst, i * size, fmt.Type, samples[i]);
        }

        public static short ToS16(float v)
        {
            if (v > 1f) v = 1f;
            else if (v < -1f) v = -1f;
            return (short)Math.Round(v * 32767f);
        }

        /// <summary>
        /// One device frame to <paramref name="outChannels"/> (1 or 2). Surround is folded into
        /// stereo: centre at -3 dB on both sides, rear/side channels at -6 dB on their side, LFE dropped.
        /// </summary>
        public static void MixDown(byte[] data, int frameOffset, DeviceFormat fmt, float[] dst, int outChannels)
        {
            int n = fmt.Channels, size = fmt.BytesPerSample;
            float c0 = ReadSample(data, frameOffset, fmt.Type);
            float c1 = n > 1 ? ReadSample(data, frameOffset + size, fmt.Type) : c0;
            if (outChannels == 1)
            {
                dst[0] = n > 1 ? (c0 + c1) * 0.5f : c0;
                return;
            }
            float l = c0, r = c1;
            if (n >= 3)
            {
                float c = ReadSample(data, frameOffset + 2 * size, fmt.Type) * 0.7071f;
                l += c;
                r += c;
            }
            for (int i = 4; i < n; i++)
            {
                float s = ReadSample(data, frameOffset + i * size, fmt.Type) * 0.5f;
                if (i % 2 == 0) l += s; else r += s;
            }
            dst[0] = l;
            dst[1] = r;
        }

        /// <summary>A stereo (or mono: l == r) frame into a device frame of any channel count.</summary>
        public static void Spread(float[] dst, int offset, int dstChannels, float l, float r)
        {
            if (dstChannels == 1)
            {
                dst[offset] = (l + r) * 0.5f;
                return;
            }
            dst[offset] = l;
            dst[offset + 1] = r;
            for (int c = 2; c < dstChannels; c++) dst[offset + c] = 0f;
        }

        /// <summary>True for digital silence (or the last bit of dither): not worth sending.</summary>
        public static bool IsSilent(short[] pcm)
        {
            foreach (var s in pcm)
                if (s > 2 || s < -2) return false;
            return true;
        }
    }

    /// <summary>
    /// Sender side: device-format frames in, wire packets out (48 kHz, 1 or 2 channels, 16-bit,
    /// <see cref="AudioWire.FramesPerPacket"/> frames each). Linear-interpolation resampler, so
    /// 44.1 kHz devices work too.
    /// </summary>
    internal sealed class CaptureConverter
    {
        readonly DeviceFormat fmt;
        readonly int outChannels;
        readonly double step;
        readonly float[] prev, cur;
        bool havePrev;
        double pos;
        short[] packet;
        int fill;

        public CaptureConverter(DeviceFormat fmt, int outChannels)
        {
            this.fmt = fmt;
            this.outChannels = outChannels;
            step = (double)fmt.Rate / AudioWire.SampleRate;
            prev = new float[outChannels];
            cur = new float[outChannels];
            packet = new short[AudioWire.FramesPerPacket * outChannels];
        }

        public void Push(byte[] data, int frames, bool silent, Action<short[]> emit)
        {
            int block = fmt.BlockAlign;
            for (int f = 0; f < frames; f++)
            {
                if (silent) Array.Clear(cur, 0, outChannels);
                else AudioDsp.MixDown(data, f * block, fmt, cur, outChannels);
                if (!havePrev)
                {
                    Array.Copy(cur, prev, outChannels);
                    havePrev = true;
                    continue;
                }
                while (pos < 1.0)
                {
                    float t = (float)pos;
                    int o = fill * outChannels;
                    for (int c = 0; c < outChannels; c++)
                        packet[o + c] = AudioDsp.ToS16(prev[c] + (cur[c] - prev[c]) * t);
                    if (++fill == AudioWire.FramesPerPacket)
                    {
                        emit(packet);
                        packet = new short[AudioWire.FramesPerPacket * outChannels];
                        fill = 0;
                    }
                    pos += step;
                }
                pos -= 1.0;
                Array.Copy(cur, prev, outChannels);
            }
        }
    }

    /// <summary>
    /// Receiver side jitter buffer. The network thread writes wire packets; the device thread reads
    /// at the device's rate and channel count. It waits for <c>targetMs</c> of audio before it
    /// starts (and again after running dry), and plays slightly faster or slower to stay near that
    /// level, because the two PCs' sound cards never run at exactly the same speed.
    /// </summary>
    internal sealed class PlaybackBuffer
    {
        const int Rate = AudioWire.SampleRate;
        const double MaxCorrection = 0.005; // ±0.5 % speed: inaudible, far more than real clock drift

        readonly object gate = new object();
        readonly int capacity, target, maxLevel;
        float[] ring = new float[0];
        int channels;
        int head, count;
        double pos;
        bool buffering = true;
        double avgLevel;
        int lastWrite = unchecked(Environment.TickCount - 3600000);

        public PlaybackBuffer(int targetMs = 40, int maxMs = 250, int capacityMs = 1000)
        {
            target = Rate * targetMs / 1000;
            maxLevel = Rate * maxMs / 1000;
            capacity = Rate * capacityMs / 1000;
        }

        /// <summary>Current playback speed adjustment (+0.001 = 0.1 % faster).</summary>
        public double Correction { get; private set; }

        public int LevelFrames { get { lock (gate) return count; } }

        public bool IsBuffering { get { lock (gate) return buffering; } }

        public bool HasRecentData(int ms) => unchecked(Environment.TickCount - System.Threading.Volatile.Read(ref lastWrite)) < ms;

        /// <summary>Adds 16-bit PCM frames (<paramref name="ch"/> = 1 or 2) starting at <paramref name="offset"/>.</summary>
        public void Write(byte[] src, int offset, int frames, int ch)
        {
            if (frames <= 0 || ch < 1 || ch > 2) return;
            lock (gate)
            {
                if (ch != channels)
                {
                    channels = ch;
                    ring = new float[capacity * ch];
                    head = count = 0;
                    pos = 0;
                    buffering = true;
                }
                if (frames > capacity)
                {
                    offset += (frames - capacity) * ch * 2;
                    frames = capacity;
                }
                int overflow = count + frames - capacity;
                if (overflow > 0) Drop(overflow);

                int tail = (head + count) % capacity;
                int o = offset;
                for (int f = 0; f < frames; f++)
                {
                    int idx = (tail + f) % capacity * ch;
                    for (int c = 0; c < ch; c++, o += 2)
                        ring[idx + c] = (short)(src[o] | src[o + 1] << 8) / 32768f;
                }
                count += frames;
                // Far behind (e.g. after the network stalled): jump back to live audio.
                if (count > maxLevel) Drop(count - target);
                System.Threading.Volatile.Write(ref lastWrite, Environment.TickCount);
            }
        }

        void Drop(int n)
        {
            if (n > count) n = count;
            head = (head + n) % capacity;
            count -= n;
            pos = Math.Max(0, pos - n);
        }

        /// <summary>
        /// Fills <paramref name="frames"/> interleaved device frames. Silence while (re)buffering.
        /// Returns false if nothing but silence was produced.
        /// </summary>
        public bool Read(float[] dst, int frames, int dstChannels, int dstRate)
        {
            lock (gate)
            {
                Array.Clear(dst, 0, frames * dstChannels);
                if (channels == 0) return false;
                if (buffering)
                {
                    if (count < target) return false;
                    buffering = false;
                    pos = 0;
                    avgLevel = count;
                }

                avgLevel = avgLevel * 0.98 + count * 0.02;
                double c = (avgLevel - target) / target * 0.01;
                Correction = c > MaxCorrection ? MaxCorrection : c < -MaxCorrection ? -MaxCorrection : c;
                double step = (double)Rate / dstRate * (1 + Correction);

                bool any = false;
                for (int f = 0; f < frames; f++)
                {
                    int i0 = (int)pos;
                    if (i0 + 1 >= count)
                    {
                        buffering = true; // ran dry: wait for a full buffer again instead of crackling
                        break;
                    }
                    float t = (float)(pos - i0);
                    int a = (head + i0) % capacity * channels;
                    int b = (head + i0 + 1) % capacity * channels;
                    float l = ring[a] + (ring[b] - ring[a]) * t;
                    float r = channels > 1 ? ring[a + 1] + (ring[b + 1] - ring[a + 1]) * t : l;
                    AudioDsp.Spread(dst, f * dstChannels, dstChannels, l, r);
                    pos += step;
                    any = true;
                }
                int used = Math.Min((int)pos, count);
                head = (head + used) % capacity;
                count -= used;
                pos -= used;
                if (buffering) pos = 0;
                return any;
            }
        }

        public void Clear()
        {
            lock (gate)
            {
                head = count = 0;
                pos = 0;
                buffering = true;
            }
        }
    }
}
