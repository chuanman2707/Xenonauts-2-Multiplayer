namespace X2Coop.Net
{
    /// <summary>
    /// Wire-protocol constants shared by host and guest.
    ///
    /// Bump <see cref="Version"/> on ANY incompatible wire change: it is baked
    /// into the LiteNetLib connect key, so mismatched builds are refused at the
    /// transport handshake before any session traffic flows.
    /// </summary>
    public static class Protocol
    {
        /// <summary>Protocol version; embedded in <see cref="ConnectKey"/>.</summary>
        public const int Version = 1;

        /// <summary>LiteNetLib connect key (host gate: ConnectionRequest.AcceptIfKey).
        /// Not const — built from <see cref="Version"/> so the two can never drift.</summary>
        public static readonly string ConnectKey = "x2coop/" + Version;

        /// <summary>Default UDP port the host listens on (guest binds ephemeral).</summary>
        public const int DefaultPort = 27960;

        /// <summary>
        /// Largest payload a higher layer may place in one message before its
        /// own envelope — T3.2 sends save chunks of exactly this size.
        /// </summary>
        public const int MaxPayloadSize = 64 * 1024;

        /// <summary>
        /// Largest single message (payload + envelope) <see cref="NetTransport"/>
        /// accepts for send, and the largest it delivers on receive.
        ///
        /// Fragmentation check (bundled FlyingWormConsole3.LiteNetLib ~0.9.x,
        /// inside Assembly-CSharp): ReliableOrdered and ReliableUnordered
        /// messages larger than MTU are fragmented AUTOMATICALLY —
        /// NetPeer.SendInternal splits into (mtu - 10)-byte fragments, up to
        /// 65535 fragments (theoretical ceiling ≈ 60-90 MiB); Unreliable /
        /// Sequenced / ReliableSequenced throw TooBigPacketException instead.
        /// Reassembly is NetPeer.AddReliablePacket (IsFragmented) which hands
        /// ONE complete message to the receive event. So a 64 KiB ReliableOrdered
        /// SaveChunk works (~47-65 UDP datagrams at MTU 1024-1432). The cap
        /// stays small anyway: every lost fragment stalls the whole ordered
        /// channel for a resend, and reassembly buffers are per-message — a
        /// protocol-level bound keeps both sane. Payload + 256 B envelope.
        /// </summary>
        public const int MaxMessageSize = MaxPayloadSize + 256;

        /// <summary>
        /// Channel map (NetManager.ChannelsCount = 4). DeliveryMethod picks the
        /// sub-channel per channel index (channel*4 + method).
        /// </summary>
        public const byte ChannelControl = 0; // control msgs, StateDelta (ReliableOrdered)
        public const byte ChannelSave = 1;    // SaveChunk stream (ReliableOrdered) - own channel so a transfer can't head-of-line block control/deltas
        public const byte ChannelPos = 2;     // T5.2 PosDelta latest-state stream (Sequenced) - a dropped packet is stale by definition, never blocks or resends
    }
}
