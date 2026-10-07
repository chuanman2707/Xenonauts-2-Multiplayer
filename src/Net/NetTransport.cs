using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using FlyingWormConsole3.LiteNetLib;
using FlyingWormConsole3.LiteNetLib.Utils;

namespace X2Coop.Net
{
    /// <summary>
    /// LiteNetLib transport wrapper around the game's bundled, PUBLIC
    /// FlyingWormConsole3.LiteNetLib.NetManager (inside Assembly-CSharp ~0.9.x:
    /// EventBasedNetListener, NetPeer, NetDataWriter/NetDataReader,
    /// DeliveryMethod — no embedded copy needed).
    ///
    /// Threading (spec §4): NetManager runs its own socket + logic threads, but
    /// every inbound callback is queued and only fires inside
    /// <see cref="PollEvents"/> — which Runner.Update calls on the Unity MAIN
    /// thread. UnsyncedEvents stays false. Consumed events are surfaced through
    /// the <see cref="TryDequeue"/> queue for the T2.2 codec; connect/disconnect
    /// /rtt are also logged here so the transport verifies standalone.
    ///
    /// Usage:
    ///   host:  StartHost(port)          — listens; guests gated by ConnectKey
    ///   guest: Connect(ip, port)        — auto Start()s on an ephemeral port
    ///   send:  Send(peerId, data, DeliveryMethod.ReliableOrdered)
    ///   drain: while (TryDequeue(out var e)) { ... }   // main thread
    ///   stop:  Stop()                   — also called by Runner destroy/quit
    ///
    /// Delivery rule (spec §4): control, save chunks and deltas →
    /// DeliveryMethod.ReliableOrdered. Only idempotent full values (clock) may
    /// use Sequenced. Deltas are NEVER sent Sequenced (it silently drops
    /// out-of-order packets).
    ///
    /// Privacy: never logs IPs/endpoints — peers are identified by NetPeer.Id.
    /// </summary>
    public sealed class NetTransport
    {
        /// <summary>Host listens, or guest dialing out.</summary>
        public enum Role : byte { None = 0, Host = 1, Guest = 2 }

        public enum NetEventKind : byte { Connected = 1, Disconnected = 2, Message = 3 }

        /// <summary>
        /// One transport event on the outbound queue. PeerId is NetPeer.Id
        /// (−1 when a guest-side connect failed before a peer existed).
        /// Data is a freshly-copied buffer (Message only). Reason is the
        /// DisconnectReason enum name (Disconnected only).
        /// </summary>
        public readonly struct NetEvent
        {
            public readonly NetEventKind Kind;
            public readonly int PeerId;
            public readonly byte[] Data;
            public readonly DeliveryMethod Delivery;
            public readonly string Reason;

            public NetEvent(NetEventKind kind, int peerId, byte[] data,
                DeliveryMethod delivery, string reason)
            {
                Kind = kind;
                PeerId = peerId;
                Data = data;
                Delivery = delivery;
                Reason = reason;
            }
        }

        private readonly EventBasedNetListener _listener;
        private readonly NetManager _manager;
        private readonly Dictionary<int, NetPeer> _peers = new Dictionary<int, NetPeer>();
        private readonly Queue<NetEvent> _events = new Queue<NetEvent>();
        private readonly Stopwatch _rttWatch = new Stopwatch();
        private Role _role = Role.None;
        private int _serverPeerId = -1; // guest: peer id of the host connection

        private const int RttLogIntervalMs = 2000;

        public NetTransport()
        {
            _listener = new EventBasedNetListener();
            _listener.PeerConnectedEvent += OnPeerConnected;
            _listener.PeerDisconnectedEvent += OnPeerDisconnected;
            _listener.NetworkReceiveEvent += OnNetworkReceive;
            _listener.NetworkErrorEvent += OnNetworkError;
            _listener.ConnectionRequestEvent += OnConnectionRequest;

            _manager = new NetManager(_listener)
            {
                DisconnectTimeout = 15000, // spec: 15 s silence = disconnect
                ChannelsCount = 4,         // ChannelControl/ChannelSave free; two spare
                UnsyncedEvents = false,    // events queue for PollEvents on the main thread
                AutoRecycle = false,       // we copy + Recycle() readers ourselves
                UpdateTime = 15,
                PingInterval = 1000,
            };
            // LiteNetLib's own NetDebug.WriteError otherwise lands on
            // UnityEngine.Debug.Log — swallowed by the game's log4net (root=ERROR).
            // Route it to our log instead (messages carry no IPs, checked at
            // decompile: bind/recv/fragment errors only).
            try { NetDebug.Logger = new LnlLogSink(); } catch { }
            _rttWatch.Start();
        }

        public Role CurrentRole => _role;
        public bool IsRunning => _manager.IsRunning;
        public int PeerCount => _peers.Count;
        public int LocalPort => _manager.IsRunning ? _manager.LocalPort : -1;
        /// <summary>Guest: peer id of the host connection, −1 otherwise.</summary>
        public int ServerPeerId => _serverPeerId;
        /// <summary>NetPeer.Id of every connected peer (snapshot).</summary>
        public int[] PeerIds
        {
            get
            {
                var ids = new int[_peers.Count];
                _peers.Keys.CopyTo(ids, 0);
                return ids;
            }
        }
        public bool IsConnected(int peerId) => _peers.ContainsKey(peerId);

        /// <summary>Host: bind a listening socket on <paramref name="port"/>.</summary>
        public bool StartHost(int port)
        {
            if (_manager.IsRunning)
            {
                Log.Warn("net host-start ignored: transport already running role=" + _role);
                return false;
            }
            _role = Role.Host;
            bool ok = TryStart(port);
            if (!ok)
            {
                // Separate-socket IPv6 bind quirks: retry IPv4-only once.
                try
                {
                    _manager.IPv6Enabled = IPv6Mode.Disabled;
                    ok = TryStart(port);
                    if (ok) { Log.Info("net host-start: IPv6 disabled retry succeeded"); }
                }
                catch (Exception ex)
                {
                    Log.Exception("net host-start ipv6-retry", ex);
                }
            }
            if (!ok)
            {
                _role = Role.None;
                Log.Warn("net host-start FAILED port=" + port);
                return false;
            }
            Log.Info("net host-start port=" + _manager.LocalPort + " key=" + Protocol.ConnectKey);
            return true;
        }

        private bool TryStart(int port)
        {
            try { return _manager.Start(port); }
            catch (Exception ex) { Log.Exception("net start " + port, ex); return false; }
        }

        /// <summary>Guest: bind an ephemeral socket (no listener intent).</summary>
        public bool StartGuest()
        {
            if (_manager.IsRunning)
            {
                Log.Warn("net guest-start ignored: transport already running role=" + _role);
                return false;
            }
            _role = Role.Guest;
            if (!TryStart(0))
            {
                _role = Role.None;
                Log.Warn("net guest-start FAILED");
                return false;
            }
            Log.Info("net guest-start port=" + _manager.LocalPort);
            return true;
        }

        /// <summary>
        /// Guest: dial the host. Auto-starts the guest socket when needed.
        /// Returns the pending peer id (connection completes asynchronously —
        /// watch the event queue for Connected / a Disconnected failure).
        /// </summary>
        public int Connect(string address, int port)
        {
            if (_role == Role.Host)
            {
                Log.Warn("net connect ignored: role=host");
                return -1;
            }
            if (!_manager.IsRunning && !StartGuest())
            {
                return -1;
            }
            try
            {
                NetPeer peer = _manager.Connect(address, port, Protocol.ConnectKey);
                if (peer == null)
                {
                    Log.Warn("net connect: request already pending (no new peer)");
                    return -1;
                }
                _serverPeerId = peer.Id;
                Log.Info("net connecting peer=" + peer.Id);
                return peer.Id;
            }
            catch (Exception ex)
            {
                Log.Exception("net connect", ex);
                return -1;
            }
        }

        /// <summary>Convenience: control-channel send (offset 0, whole buffer).</summary>
        public bool Send(int peerId, byte[] data, DeliveryMethod method)
        {
            return Send(peerId, Protocol.ChannelControl, data, 0, data?.Length ?? 0, method);
        }

        /// <summary>
        /// Send to one peer. Over-MTU payloads fragment automatically on
        /// ReliableOrdered/ReliableUnordered (see Protocol.MaxMessageSize); the
        /// cap check + TooBigPacketException guard keep failures logged, not thrown.
        /// </summary>
        public bool Send(int peerId, byte channel, byte[] data, int offset, int length, DeliveryMethod method)
        {
            if (data == null || length <= 0) { return false; }
            if (length > Protocol.MaxMessageSize)
            {
                Log.Warn("net send dropped: len=" + length + " > max=" + Protocol.MaxMessageSize);
                return false;
            }
            if (!_peers.TryGetValue(peerId, out NetPeer peer) || peer == null)
            {
                Log.Warn("net send dropped: no peer=" + peerId);
                return false;
            }
            try
            {
                peer.Send(data, offset, length, channel, method);
                return true;
            }
            catch (TooBigPacketException ex)
            {
                Log.Warn("net send dropped: " + ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                Log.Exception("net send", ex);
                return false;
            }
        }

        /// <summary>
        /// LiteNetLib's real single-packet budget for one peer + delivery
        /// method (NetPeer.GetMaxSinglePacketSize: MTU minus the method's
        /// packet header). T5.2-r1 (review I-1): ReliableOrdered /
        /// ReliableUnordered fragment over-MTU payloads automatically, but
        /// Sequenced / ReliableSequenced / Unreliable THROW
        /// TooBigPacketException - callers on those lanes must split to this
        /// budget themselves. -1 when the peer is gone or the call fails.
        /// </summary>
        public int MaxSinglePacketSize(int peerId, DeliveryMethod method)
        {
            try
            {
                NetPeer peer;
                return _peers.TryGetValue(peerId, out peer) && peer != null
                    ? peer.GetMaxSinglePacketSize(method)
                    : -1;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>Send the same payload to every connected peer; returns recipient count.</summary>
        public int Broadcast(byte[] data, int offset, int length, DeliveryMethod method,
            byte channel = Protocol.ChannelControl)
        {
            int sent = 0;
            foreach (int id in PeerIds)
            {
                if (Send(id, channel, data, offset, length, method)) { sent++; }
            }
            return sent;
        }

        /// <summary>Ask one peer to disconnect (graceful).</summary>
        public void Disconnect(int peerId)
        {
            if (_peers.TryGetValue(peerId, out NetPeer peer) && peer != null)
            {
                try { _manager.DisconnectPeer(peer); }
                catch (Exception ex) { Log.Exception("net disconnect " + peerId, ex); }
            }
        }

        /// <summary>
        /// Drain LiteNetLib's queued events (fires our OnXxx handlers) then emit
        /// the 2 s RTT heartbeat. Call every frame from the Unity main thread.
        /// </summary>
        public void PollEvents()
        {
            _manager.PollEvents();
            if (_rttWatch.ElapsedMilliseconds >= RttLogIntervalMs)
            {
                _rttWatch.Restart();
                if (_peers.Count > 0)
                {
                    foreach (KeyValuePair<int, NetPeer> kv in _peers)
                    {
                        // NetPeer.Ping is the smoothed one-way latency (avgRtt/2);
                        // rtt = round-trip estimate.
                        Log.Info("net rtt peer=" + kv.Key + " rtt=" + (kv.Value.Ping * 2) + "ms mtu=" + kv.Value.Mtu);
                    }
                }
            }
        }

        /// <summary>Next queued transport event for the session layer (T2.2+).</summary>
        public bool TryDequeue(out NetEvent evt)
        {
            if (_events.Count == 0)
            {
                evt = default(NetEvent);
                return false;
            }
            evt = _events.Dequeue();
            return true;
        }

        /// <summary>Stop sockets/threads, drop peers and any undelivered events.</summary>
        public void Stop()
        {
            Role was = _role;
            try
            {
                if (_manager.IsRunning)
                {
                    _manager.Stop(); // sends graceful disconnects, joins threads
                }
            }
            catch (Exception ex)
            {
                Log.Exception("net stop", ex);
            }
            _peers.Clear();
            _events.Clear();
            _serverPeerId = -1;
            _role = Role.None;
            if (was != Role.None)
            {
                Log.Info("net stop role=" + was);
            }
        }

        // --- LiteNetLib event handlers (all run on the PollEvents caller's
        //     thread = Unity main thread) -------------------------------------

        private void OnConnectionRequest(ConnectionRequest request)
        {
            try
            {
                if (_role != Role.Host)
                {
                    Log.Warn("net reject: inbound request while role=" + _role);
                    request.Reject();
                    return;
                }
                NetPeer peer = request.AcceptIfKey(Protocol.ConnectKey);
                if (peer == null)
                {
                    // Wrong key/version, or a duplicate already handled.
                    Log.Warn("net reject: bad connect key");
                }
                // Accepted: the Connect event is queued and lands next PollEvents.
            }
            catch (Exception ex)
            {
                Log.Exception("net conn-request", ex);
            }
        }

        private void OnPeerConnected(NetPeer peer)
        {
            try
            {
                if (peer == null) { return; }
                _peers[peer.Id] = peer;
                _events.Enqueue(new NetEvent(NetEventKind.Connected, peer.Id, null, DeliveryMethod.ReliableOrdered, null));
                Log.Info("net connected peer=" + peer.Id + " role=" + _role + " mtu=" + peer.Mtu);
            }
            catch (Exception ex)
            {
                Log.Exception("net peer-connected", ex);
            }
        }

        private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            try
            {
                if (peer == null)
                {
                    // Guest-side connect attempt that never established a peer
                    // (UnknownHost / ConnectionFailed / ConnectionRejected).
                    _events.Enqueue(new NetEvent(NetEventKind.Disconnected, -1, null, DeliveryMethod.Unreliable, info.Reason.ToString()));
                    Log.Warn("net connect-failed reason=" + info.Reason);
                    _serverPeerId = -1;
                    return;
                }
                _peers.Remove(peer.Id);
                if (peer.Id == _serverPeerId) { _serverPeerId = -1; }
                _events.Enqueue(new NetEvent(NetEventKind.Disconnected, peer.Id, null, DeliveryMethod.Unreliable, info.Reason.ToString()));
                Log.Info("net disconnected peer=" + peer.Id + " reason=" + info.Reason);
            }
            catch (Exception ex)
            {
                Log.Exception("net peer-disconnected", ex);
            }
        }

        private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, DeliveryMethod method)
        {
            try
            {
                int n = reader.AvailableBytes;
                if (n > Protocol.MaxMessageSize)
                {
                    Log.Warn("net msg dropped: len=" + n + " > max=" + Protocol.MaxMessageSize);
                    reader.Recycle();
                    return;
                }
                byte[] buf = reader.GetRemainingBytes(); // copy out of the pooled packet
                reader.Recycle();                        // AutoRecycle=false: required
                _events.Enqueue(new NetEvent(NetEventKind.Message, peer != null ? peer.Id : -1, buf, method, null));
            }
            catch (Exception ex)
            {
                Log.Exception("net recv", ex);
                try { reader.Recycle(); } catch { }
            }
        }

        private void OnNetworkError(System.Net.IPEndPoint endPoint, SocketError socketError)
        {
            // endPoint deliberately NOT logged (no IPs in logs).
            Log.Warn("net socket-error code=" + socketError);
        }

        /// <summary>Routes LiteNetLib NetDebug errors into our own log (WARN).</summary>
        private sealed class LnlLogSink : INetLogger
        {
            public void WriteNet(NetLogLevel level, string str, params object[] args)
            {
                try { Log.Warn("lnl " + string.Format(str, args)); }
                catch { try { Log.Warn("lnl " + str); } catch { } }
            }
        }
    }
}
