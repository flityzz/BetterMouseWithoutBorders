using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace BetterMouse
{
    internal sealed class AuthException : Exception
    {
        public AuthException(string message) : base(message) { }
    }

    internal sealed class ProtocolException : Exception
    {
        public ProtocolException(string message) : base(message) { }
    }

    /// <summary>A stream of frames sent at low priority (clipboard transfers). Null = finished.</summary>
    internal interface IFrameSource : IDisposable
    {
        byte[] NextFrame();
    }

    /// <summary>
    /// One authenticated, encrypted TCP link to the other PC. Input goes through a priority
    /// queue ahead of bulk data, a heartbeat flows every 500 ms, and silence for
    /// <see cref="TimeoutMs"/> closes the link so the owner can reconnect.
    /// </summary>
    internal sealed class Connection
    {
        public const int PingIntervalMs = 500;
        /// <summary>
        /// Heartbeat while the mouse is in use. Wi-Fi adapters doze after ~100-200 ms without
        /// traffic, and the first packets after a doze can wait 100-500 ms at the access point:
        /// that shows up as the cursor freezing for a moment. Frequent tiny packets keep both
        /// radios awake (a few hundred bytes per second).
        /// </summary>
        public const int WarmPingIntervalMs = 100;
        const int WarmForMs = 3000;
        const int SlowRttMs = 150;
        public const int TimeoutMs = 4000;
        const int MaxFrame = 1 << 20;
        const int HelloSize = 4 + 2 + 1 + 32;
        static readonly byte[] Magic = { (byte)'B', (byte)'M', (byte)'W', (byte)'B' };

        readonly TcpClient client;
        readonly NetworkStream stream;
        readonly SecureChannel channel;
        readonly ConcurrentQueue<byte[]> urgent = new ConcurrentQueue<byte[]>();
        readonly ConcurrentQueue<IFrameSource> bulk = new ConcurrentQueue<IFrameSource>();
        readonly AutoResetEvent signal = new AutoResetEvent(false);
        readonly ManualResetEventSlim byeFlushed = new ManualResetEventSlim(false);
        bool byePending;
        Action<Connection, byte[]> onMessage;
        Action<Connection, string> onClosed;
        int closed;
        int lastReceive = Environment.TickCount;
        int warmUntil;
        int lastRtt = -1, worstRtt, slowCount, lastSlowLog = Environment.TickCount;

        /// <summary>Time since anything (data or heartbeat) arrived from the other PC.</summary>
        public int SilenceMs => unchecked(Environment.TickCount - Volatile.Read(ref lastReceive));

        /// <summary>Latest heartbeat round trip in ms (-1 = not measured yet).</summary>
        public int LastRttMs => Volatile.Read(ref lastRtt);

        /// <summary>Worst round trip since the link came up.</summary>
        public int WorstRttMs => Volatile.Read(ref worstRtt);

        /// <summary>The mouse is in use: use fast heartbeats for a few seconds (cheap; call freely).</summary>
        public void KeepWarm()
        {
            int until = Environment.TickCount + WarmForMs;
            if (unchecked(until - Volatile.Read(ref warmUntil)) <= 200) return;
            bool wasWarm = IsWarm;
            Volatile.Write(ref warmUntil, until);
            if (!wasWarm) signal.Set(); // writer may be in a 500 ms wait: switch to fast heartbeats now
        }

        bool IsWarm => unchecked(Volatile.Read(ref warmUntil) - Environment.TickCount) > 0;

        public string PeerName { get; }
        public IPAddress PeerAddress { get; }
        public ManualResetEvent ClosedEvent { get; } = new ManualResetEvent(false);
        public bool IsClosed => Volatile.Read(ref closed) != 0;

        Connection(TcpClient client, NetworkStream stream, SecureChannel channel, string peerName)
        {
            this.client = client;
            this.stream = stream;
            this.channel = channel;
            PeerName = peerName;
            var ip = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
            PeerAddress = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
        }

        /// <summary>
        /// Runs the handshake on a freshly connected socket. Both sides exchange random nonces,
        /// derive per-session keys from the shared security key, then prove they hold it by
        /// exchanging an encrypted Welcome. Throws <see cref="AuthException"/> on a key mismatch.
        /// </summary>
        public static Connection Establish(TcpClient client, Role role, byte[] masterKey, string localName)
        {
            client.NoDelay = true;
            client.SendBufferSize = 64 * 1024;
            var s = client.GetStream();
            s.ReadTimeout = TimeoutMs;
            s.WriteTimeout = TimeoutMs;

            var nonce = Crypto.RandomBytes(32);
            var hello = new byte[HelloSize];
            Buffer.BlockCopy(Magic, 0, hello, 0, 4);
            hello[4] = (byte)AppInfo.ProtocolVersion;
            hello[5] = (byte)(AppInfo.ProtocolVersion >> 8);
            hello[6] = (byte)role;
            Buffer.BlockCopy(nonce, 0, hello, 7, 32);
            s.Write(hello, 0, hello.Length);

            var peer = ReadExact(s, HelloSize);
            for (int i = 0; i < 4; i++)
                if (peer[i] != Magic[i]) throw new ProtocolException("The other side is not " + AppInfo.Name);
            int version = peer[4] | peer[5] << 8;
            if (version != AppInfo.ProtocolVersion)
                throw new ProtocolException($"Version mismatch (this PC speaks v{AppInfo.ProtocolVersion}, the other v{version}). Use the same version on both PCs.");
            var peerRole = (Role)peer[6];
            if (peerRole == role)
                throw new ProtocolException(role == Role.Host
                    ? "Both PCs are set to Host. Set the other PC to Client."
                    : "Both PCs are set to Client. Set one PC to Host.");
            var peerNonce = new byte[32];
            Buffer.BlockCopy(peer, 7, peerNonce, 0, 32);

            var hostNonce = role == Role.Host ? nonce : peerNonce;
            var clientNonce = role == Role.Host ? peerNonce : nonce;
            var session = Crypto.Hmac(masterKey, Crypto.Label("bmwb session"), hostNonce, clientNonce);
            var h2cEnc = Crypto.Hmac(session, Crypto.Label("host->client enc"));
            var h2cMac = Crypto.Hmac(session, Crypto.Label("host->client mac"));
            var c2hEnc = Crypto.Hmac(session, Crypto.Label("client->host enc"));
            var c2hMac = Crypto.Hmac(session, Crypto.Label("client->host mac"));
            var channel = role == Role.Host
                ? new SecureChannel(h2cEnc, h2cMac, c2hEnc, c2hMac)
                : new SecureChannel(c2hEnc, c2hMac, h2cEnc, h2cMac);

            try
            {
                var welcome = channel.Seal(Msg.Welcome(localName, AppInfo.Version));
                s.Write(welcome, 0, welcome.Length);

                var frame = ReadFrame(s, channel);
                if (frame == null)
                    throw new AuthException("The security key does not match the other PC");
                if (frame.Length == 0 || frame[0] != (byte)MsgType.Welcome)
                    throw new ProtocolException("Unexpected handshake message");
                var r = new PacketReader(frame);
                var name = r.String();
                var peerVersion = r.String();
                Log.Info($"Handshake OK with {name} (version {peerVersion})");
                return new Connection(client, s, channel, name);
            }
            catch
            {
                channel.Dispose();
                throw;
            }
        }

        public void Start(Action<Connection, byte[]> messageHandler, Action<Connection, string> closedHandler)
        {
            onMessage = messageHandler;
            onClosed = closedHandler;
            new Thread(ReadLoop) { IsBackground = true, Name = "BetterMouse reader", Priority = ThreadPriority.AboveNormal }.Start();
            new Thread(WriteLoop) { IsBackground = true, Name = "BetterMouse writer", Priority = ThreadPriority.AboveNormal }.Start();
        }

        /// <summary>Queues an input/control message; never blocks (safe from the input hook).</summary>
        public void Send(byte[] message)
        {
            if (IsClosed) return;
            urgent.Enqueue(message);
            signal.Set();
        }

        /// <summary>Queues a low-priority stream (clipboard). It is disposed when done or on close.</summary>
        public void SendBulk(IFrameSource source)
        {
            if (IsClosed) { source.Dispose(); return; }
            bulk.Enqueue(source);
            signal.Set();
        }

        /// <summary>Tells the other side we are leaving on purpose (so it does not wait for a timeout), then closes.</summary>
        public void CloseGracefully(string reason)
        {
            if (IsClosed) return;
            Send(Msg.Bye);
            byeFlushed.Wait(300);
            Close(reason);
        }

        public void Close(string reason)
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            try { client.Close(); } catch { }
            signal.Set();
            ClosedEvent.Set();
            try { onClosed?.Invoke(this, reason); }
            catch (Exception ex) { Log.Error("Close handler failed", ex); }
        }

        void ReadLoop()
        {
            try
            {
                while (!IsClosed)
                {
                    var message = ReadFrame(stream, channel);
                    if (message == null) throw new ProtocolException("Corrupted or tampered data received");
                    Volatile.Write(ref lastReceive, Environment.TickCount);
                    if (message.Length == 0) continue;
                    switch ((MsgType)message[0])
                    {
                        case MsgType.Ping:
                            if (message.Length >= 5) Send(Msg.PongFor(message));
                            continue;
                        case MsgType.Pong:
                            if (message.Length >= 5) RecordRtt(unchecked(Environment.TickCount - (int)SecureChannel.ReadUInt32(message, 1)));
                            continue;
                        case MsgType.Bye:
                            Close("The other PC closed the connection");
                            return;
                    }
                    try { onMessage?.Invoke(this, message); }
                    catch (Exception ex) { Log.Error($"Handling message {(MsgType)message[0]} failed", ex); }
                }
            }
            catch (Exception ex)
            {
                Close(Describe(ex));
            }
        }

        void RecordRtt(int rtt)
        {
            if (rtt < 0 || rtt > 60000) return;
            Volatile.Write(ref lastRtt, rtt);
            if (rtt > worstRtt) Volatile.Write(ref worstRtt, rtt);
            if (rtt < SlowRttMs) return;
            slowCount++;
            // At most one log line per 10 s, so a bad network doesn't flood the log.
            if (unchecked(Environment.TickCount - lastSlowLog) < 10000) return;
            Log.Warn($"Network delay to {PeerName}: {rtt} ms round trip ({slowCount} slow heartbeat(s) recently, worst so far {worstRtt} ms). " +
                     "Wi-Fi power saving or a busy network makes the cursor stutter.");
            slowCount = 0;
            lastSlowLog = Environment.TickCount;
        }

        void WriteLoop()
        {
            var batch = new MemoryStream(64 * 1024);
            var sincePing = System.Diagnostics.Stopwatch.StartNew();
            IFrameSource active = null;
            try
            {
                while (!IsClosed)
                {
                    int interval = IsWarm ? WarmPingIntervalMs : PingIntervalMs;
                    signal.WaitOne(interval);
                    if (IsClosed) break;

                    while (urgent.TryDequeue(out var m))
                    {
                        Append(batch, m);
                        if (batch.Length >= 32 * 1024) Flush(batch);
                    }

                    // At most one bulk frame per round, so input never waits behind a big transfer.
                    if (active == null) bulk.TryDequeue(out active);
                    if (active != null)
                    {
                        byte[] frame;
                        try { frame = active.NextFrame(); }
                        catch (Exception ex) { Log.Error("Clipboard transfer failed", ex); frame = null; }
                        if (frame == null) { active.Dispose(); active = null; }
                        else Append(batch, frame);
                        if (active != null || !bulk.IsEmpty) signal.Set();
                    }

                    // Timestamped heartbeat on schedule even while data flows, so the round trip
                    // is measured all the time and the other side always hears from us.
                    if (sincePing.ElapsedMilliseconds >= interval)
                    {
                        Append(batch, Msg.PingAt(Environment.TickCount));
                        sincePing.Restart();
                    }

                    if (batch.Length > 0) Flush(batch);
                }
            }
            catch (Exception ex)
            {
                Close(Describe(ex));
            }
            finally
            {
                active?.Dispose();
                while (bulk.TryDequeue(out var b)) b.Dispose();
            }
        }

        // Append/Flush run on the writer thread only: the cipher stream and sequence numbers
        // must advance in exactly the order frames hit the wire.
        void Append(MemoryStream batch, byte[] message)
        {
            if (ReferenceEquals(message, Msg.Bye)) byePending = true;
            var sealedFrame = channel.Seal(message);
            batch.Write(sealedFrame, 0, sealedFrame.Length);
        }

        void Flush(MemoryStream batch)
        {
            stream.Write(batch.GetBuffer(), 0, (int)batch.Length);
            batch.SetLength(0);
            if (byePending) byeFlushed.Set();
        }

        /// <summary>Reads one frame; returns null if authentication fails.</summary>
        static byte[] ReadFrame(NetworkStream s, SecureChannel channel)
        {
            var lenBytes = ReadExact(s, 4);
            var len = SecureChannel.ReadUInt32(lenBytes, 0);
            if (len > MaxFrame) throw new ProtocolException("Frame too large");
            var body = ReadExact(s, (int)len + SecureChannel.TagSize);
            if (!channel.Open(body, 0, (int)len)) return null;
            var plain = new byte[len];
            Buffer.BlockCopy(body, 0, plain, 0, (int)len);
            return plain;
        }

        static byte[] ReadExact(Stream s, int count)
        {
            var buf = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buf, read, count - read);
                if (n <= 0) throw new EndOfStreamException("The other PC closed the connection");
                read += n;
            }
            return buf;
        }

        static string Describe(Exception ex)
        {
            if (ex is IOException && ex.InnerException is SocketException se)
            {
                if (se.SocketErrorCode == SocketError.TimedOut) return $"No response for {TimeoutMs / 1000} s (network down?)";
                return se.Message;
            }
            if (ex is ObjectDisposedException) return "Connection closed";
            return ex.Message;
        }
    }
}
