using System;
using System.IO;
using System.Text;

namespace BetterMouse
{
    internal enum MsgType : byte
    {
        Welcome = 1,
        /// <summary>Heartbeat; may carry the sender's tick count, which the receiver echoes in a Pong.</summary>
        Ping = 2,
        Bye = 3,
        /// <summary>Echo of a Ping's tick count, to measure the round trip.</summary>
        Pong = 4,

        /// <summary>Sender hands the cursor to the receiver (edge of the receiver's screen + position).</summary>
        Enter = 10,
        /// <summary>Controlled PC: the cursor left through the edge it came in from; take it back.</summary>
        Leave = 11,
        /// <summary>Controller: control is over (hotkey/disconnect); release every key and button.</summary>
        EndControl = 12,
        /// <summary>"The other PC is on my …" + when that was chosen; the receiver mirrors the newer choice.</summary>
        Layout = 13,
        /// <summary>The user is really using the sender right now (sent at most every 30 s while active).</summary>
        Activity = 14,

        MouseMove = 20,
        MouseButton = 21,
        Wheel = 22,
        Key = 23,

        ClipBegin = 30,
        ClipEntry = 31,
        ClipData = 32,
        ClipEnd = 33,
        ClipCancel = 34,
    }

    internal enum MouseButton : byte { Left = 1, Right = 2, Middle = 3, X1 = 4, X2 = 5 }

    internal enum ClipKind : byte { Text = 1, Image = 2, Files = 3 }

    internal static class AppInfo
    {
        /// <summary>Display name: windows, tray, dialogs, firewall rule, autostart.</summary>
        public const string Name = "Better Mouse Without Borders";
        /// <summary>File/folder name: exe, settings folder, ini, log.</summary>
        public const string FileName = "BetterMouseWithoutBorders";
        /// <summary>Name used up to 1.3.0 (migrated automatically).</summary>
        public const string LegacyFileName = "BetterMouse";
        public const int ProtocolVersion = 1;
        public static readonly string Version = typeof(AppInfo).Assembly.GetName().Version.ToString(3);
    }

    internal sealed class PacketWriter
    {
        readonly MemoryStream ms = new MemoryStream();
        readonly BinaryWriter w;

        public PacketWriter(MsgType type)
        {
            w = new BinaryWriter(ms, Encoding.UTF8);
            w.Write((byte)type);
        }

        public PacketWriter Byte(byte v) { w.Write(v); return this; }
        public PacketWriter Bool(bool v) { w.Write(v); return this; }
        public PacketWriter Int16(short v) { w.Write(v); return this; }
        public PacketWriter UInt16(ushort v) { w.Write(v); return this; }
        public PacketWriter Int32(int v) { w.Write(v); return this; }
        public PacketWriter Int64(long v) { w.Write(v); return this; }
        public PacketWriter Double(double v) { w.Write(v); return this; }

        public PacketWriter String(string v)
        {
            var b = Encoding.UTF8.GetBytes(v ?? "");
            w.Write(b.Length);
            w.Write(b);
            return this;
        }

        public PacketWriter Bytes(byte[] data, int offset, int count) { w.Write(data, offset, count); return this; }

        public byte[] ToArray() { w.Flush(); return ms.ToArray(); }
    }

    internal sealed class PacketReader
    {
        readonly byte[] b;
        int pos;

        public PacketReader(byte[] message) { b = message; pos = 1; } // skip the type byte

        public int Remaining => b.Length - pos;

        void Need(int n) { if (pos + n > b.Length) throw new InvalidDataException("Truncated message"); }

        public byte Byte() { Need(1); return b[pos++]; }
        public bool Bool() => Byte() != 0;
        public short Int16() { Need(2); var v = BitConverter.ToInt16(b, pos); pos += 2; return v; }
        public ushort UInt16() { Need(2); var v = BitConverter.ToUInt16(b, pos); pos += 2; return v; }
        public int Int32() { Need(4); var v = BitConverter.ToInt32(b, pos); pos += 4; return v; }
        public long Int64() { Need(8); var v = BitConverter.ToInt64(b, pos); pos += 8; return v; }
        public double Double() { Need(8); var v = BitConverter.ToDouble(b, pos); pos += 8; return v; }

        public string String()
        {
            int n = Int32();
            if (n < 0) throw new InvalidDataException("Bad string length");
            Need(n);
            var s = Encoding.UTF8.GetString(b, pos, n);
            pos += n;
            return s;
        }

        public ArraySegment<byte> Rest()
        {
            var seg = new ArraySegment<byte>(b, pos, b.Length - pos);
            pos = b.Length;
            return seg;
        }
    }

    internal static class Msg
    {
        public static readonly byte[] Ping = { (byte)MsgType.Ping };
        public static readonly byte[] Bye = { (byte)MsgType.Bye };

        public static byte[] PingAt(int tick)
        {
            var b = new byte[5];
            b[0] = (byte)MsgType.Ping;
            SecureChannel.WriteUInt32(b, 1, unchecked((uint)tick));
            return b;
        }

        public static byte[] PongFor(byte[] ping)
        {
            var b = (byte[])ping.Clone();
            b[0] = (byte)MsgType.Pong;
            return b;
        }
        public static readonly byte[] EndControl = { (byte)MsgType.EndControl };
        public static readonly byte[] Activity = { (byte)MsgType.Activity };

        public static byte[] Welcome(string machineName, string appVersion) =>
            new PacketWriter(MsgType.Welcome).String(machineName).String(appVersion).ToArray();

        /// <param name="ratio">Position along the edge, 0..1; negative = centre of the primary screen.</param>
        public static byte[] Enter(Edge edgeOfReceiver, double ratio) =>
            new PacketWriter(MsgType.Enter).Byte((byte)edgeOfReceiver).Double(ratio).ToArray();

        public static byte[] Leave(double ratio) => new PacketWriter(MsgType.Leave).Double(ratio).ToArray();

        public static byte[] Layout(Edge peerSide, long changedUtcTicks) =>
            new PacketWriter(MsgType.Layout).Byte((byte)peerSide).Int64(changedUtcTicks).ToArray();

        // Mouse moves are the hot path: hand-rolled to avoid allocations beyond the array itself.
        public static byte[] MouseMove(int dx, int dy)
        {
            var b = new byte[9];
            b[0] = (byte)MsgType.MouseMove;
            SecureChannel.WriteUInt32(b, 1, (uint)dx);
            SecureChannel.WriteUInt32(b, 5, (uint)dy);
            return b;
        }

        public static byte[] MouseButton(MouseButton button, bool down) =>
            new[] { (byte)MsgType.MouseButton, (byte)button, (byte)(down ? 1 : 0) };

        public static byte[] Wheel(short delta, bool horizontal) =>
            new[] { (byte)MsgType.Wheel, (byte)delta, (byte)(delta >> 8), (byte)(horizontal ? 1 : 0) };

        public static byte[] Key(ushort vk, ushort scan, bool extended, bool up) =>
            new[]
            {
                (byte)MsgType.Key, (byte)vk, (byte)(vk >> 8), (byte)scan, (byte)(scan >> 8),
                (byte)((extended ? 1 : 0) | (up ? 2 : 0))
            };

        public static byte[] ClipBegin(int id, ClipKind kind, long totalBytes, int entries) =>
            new PacketWriter(MsgType.ClipBegin).Int32(id).Byte((byte)kind).Int64(totalBytes).Int32(entries).ToArray();

        public static byte[] ClipEntry(int id, string relativePath, long length, bool isDirectory) =>
            new PacketWriter(MsgType.ClipEntry).Int32(id).String(relativePath).Int64(length).Bool(isDirectory).ToArray();

        public static byte[] ClipData(int id, byte[] data, int offset, int count) =>
            new PacketWriter(MsgType.ClipData).Int32(id).Bytes(data, offset, count).ToArray();

        public static byte[] ClipEnd(int id) => new PacketWriter(MsgType.ClipEnd).Int32(id).ToArray();

        public static byte[] ClipCancel(int id) => new PacketWriter(MsgType.ClipCancel).Int32(id).ToArray();
    }
}
