using System;

namespace X2Coop.Net
{
    /// <summary>
    /// Wire-message types for protocol v1 (spec section 6). Every message on the
    /// wire is: [MsgType:1][sessionId:16][payload] — the envelope lives in
    /// <see cref="MsgCodec"/>; this file is payload data only.
    ///
    /// MsgType values are EXPLICIT: never reorder or renumber. Additions pick
    /// the next free value so shipped builds never see shifted meanings.
    /// Values 20-21 are the T5.5 hash-verification pair (plan T5.5:
    /// HashReq{seq}, HashResp{seq, hash, count}); they are part of protocol v1
    /// here even though no sender exists until T5.5 lands.
    /// </summary>
    public enum MsgType : byte
    {
        // -- handshake -------------------------------------------------------
        Hello = 1,          // G->H  versions + player + pack stamps
        HelloAck = 2,       // H->G  accept / reject+reason
        // -- save transfer ---------------------------------------------------
        SaveOffer = 3,      // H->G  campaignId + baselineSeq + byteCount + sha256
        SaveChunk = 4,      // H->G  offset + <=64 KiB
        GuestReady = 5,     // G->H  "SaveAck": save loaded, ready for deltas
        // -- live replication --------------------------------------------------
        StateDelta = 6,     // H->G  seq + entity/component ops
        // -- recovery / session state ------------------------------------------
        ResyncReq = 7,      // G->H  reason -> host answers with new SaveOffer
        SessionNotice = 8,  // both  GuestJoined/GuestLeft/HostEnded/...
        // -- keepalive ---------------------------------------------------------
        Ping = 9,           // both  tick echo (2 s keepalive; 15 s = timeout)
        Pong = 10,          // both  reply echoing Ping.Ticks
        // -- live replication (latest-state lane) ------------------------------
        PosDelta = 11,      // H->G  T5.2: positions/clocks at ~2 Hz, Sequenced on ChannelPos
        // -- T5.5 desync verification -----------------------------------------
        HashReq = 20,       // H->G  compute world hash at seq
        HashResp = 21,      // G->H  seq + sha256 + hashed-item count
    }

    /// <summary>Why HelloAck refuses a join (HelloRejectReason.None = accepted).</summary>
    public enum HelloRejectReason : byte
    {
        None = 0,
        VersionMismatch = 1,   // protocolVer or modVer differs
        PackMismatch = 2,      // activePackStamps differ
        NotOnStrategy = 3,     // host is not on the Strategy screen
        SessionFull = 4,       // a guest is already attached (S1 = 1 guest)
    }

    /// <summary>GuestReady outcome (spec "SaveAck/GuestReady").</summary>
    public enum GuestReadyStatus : byte
    {
        Ready = 1,             // save loaded, world initialized, ready for deltas
        LoadFailed = 2,        // SaveGameSystem.LoadGame rejected the file
        WorldInitFailed = 3,   // loaded but world did not come up (bad state)
    }

    /// <summary>Why a guest asks for a full resync.</summary>
    public enum ResyncReason : byte
    {
        MissedDelta = 1,       // seq gap observed (reorder/loss beyond repair)
        HashMismatch = 2,      // T5.5 HashResp did not match host hash
        SaveLoadFailed = 3,    // guest failed to load a previous SaveOffer stream
        HostRequested = 4,     // host decided the guest must resync (e.g. battle return)
        ManualRequest = 5,     // T3.6: guest pressed Resync / periodic resyncMinutes timer
    }

    /// <summary>SessionNotice kinds (spec section 6).</summary>
    public enum SessionNoticeKind : byte
    {
        GuestJoined = 1,
        GuestLeft = 2,
        HostEnded = 3,
        HostLeftStrategy = 4,
        HostReturned = 5,
        Resynced = 6,
        GuestMirrorLost = 7,   // T3.6 G->H: guest's mirror world disposed mid-session (detached)
    }

    /// <summary>StateDelta operation kinds (the T5.2 coalescer emits these).</summary>
    public enum DeltaOpKind : byte
    {
        /// <summary>Component value write; Payload = serialized component.</summary>
        SetComponent = 1,
        /// <summary>New entity; Payload = serialized component bag (T5.x format).</summary>
        CreateEntity = 2,
        /// <summary>Entity removed; ComponentType/Payload unused.</summary>
        DeleteEntity = 3,
        /// <summary>Component removed from a live entity (rare); Payload unused.</summary>
        RemoveComponent = 4,
    }

    /// <summary>
    /// Bounds applied by the codec. Every length-prefixed field validates
    /// against these BEFORE allocating - a malformed packet can never force a
    /// huge allocation or a read past the buffer.
    /// </summary>
    internal static class WireLimits
    {
        public const int SessionIdBytes = 16;        // Guid.ToByteArray size
        public const int Sha256Bytes = 32;
        public const int CommitHashCount = 4;        // artitas, assets, common, x2core (Commits.GetEnumerator order)

        public const int MaxModVersionBytes = 64;
        public const int MaxGameVersionBytes = 128;
        public const int MaxCommitHashBytes = 128;   // git hashes are ~40 chars
        public const int MaxPlayerNameBytes = 64;
        public const int MaxPackUidBytes = 128;
        public const int MaxPackVersionBytes = 64;
        public const int MaxPackStamps = 64;
        public const int MaxResyncDetailBytes = 256;
        public const int MaxHelloAckDetailBytes = 256; // human-readable reject text
        public const int MaxComponentTypeBytes = 1024;  // generic FullNames (MutationDelayedItemComponent`2<...>) comfortably exceed 256
        public const int MaxCampaignNameBytes = 128; // SaveOffer.campaignName (T3.2 review: CampaignId can be Guid.Empty)

        public const int MaxDeltaOps = 4096;         // sanity cap; the frame budget keeps real batches far smaller
        public const int MaxOpPayloadBytes = 32 * 1024;
        public const int MaxChunkBytes = Protocol.MaxPayloadSize; // 64 KiB
    }

    /// <summary>One active-pack stamp carried by Hello (uid + version).</summary>
    public struct PackStamp
    {
        public string Uid;
        public string Version;
    }

    /// <summary>One entity/component operation inside a StateDelta batch.</summary>
    public struct DeltaOp
    {
        public int EntityId;             // Artitas Entity.ID is int32
        public DeltaOpKind Kind;
        public string ComponentType;     // full type name; empty for DeleteEntity
        public byte[] Payload;           // serialized component data; empty allowed
    }

    /// <summary>
    /// Base for all wire messages. SessionId is the ENVELOPE field: codec
    /// writes it on encode, fills it on decode. Guid.Empty is the pre-session
    /// sentinel (Hello, and HelloAck rejects); a live session carries a real id.
    /// </summary>
    public abstract class NetMessage
    {
        /// <summary>Envelope session id. Empty until a session is assigned.</summary>
        public Guid SessionId;

        public abstract MsgType Type { get; }

        /// <summary>Write payload. Return false on a bound violation (caller bug - encode fails).</summary>
        internal abstract bool WriteBody(MsgWriter w);

        /// <summary>Read payload. Fields are already bounds-checked by <see cref="MsgReader"/>.</summary>
        internal abstract void ReadBody(MsgReader r);

        /// <summary>Encode-size hint so big messages (SaveChunk) skip resize churn.</summary>
        internal virtual int SizeHint => 128;
    }

    /// <summary>
    /// G->H join request. sessionId is Empty (pre-session). CommitHashes are the
    /// game's four repo hashes in Commits.GetEnumerator order (artitas, assets,
    /// common, x2core) - together with GameVersion they are the "gameVer+commits"
    /// parity check. CampaignId is null for a fresh lobby join.
    /// </summary>
    public sealed class HelloMsg : NetMessage
    {
        public int ProtocolVersion;
        public string ModVersion = string.Empty;
        public string GameVersion = string.Empty;
        public string[] CommitHashes = new string[WireLimits.CommitHashCount];
        public string PlayerName = string.Empty;   // transported, NEVER logged
        public PackStamp[] Packs = new PackStamp[0];
        public Guid? CampaignId;

        public override MsgType Type => MsgType.Hello;

        internal override bool WriteBody(MsgWriter w)
        {
            w.Put(ProtocolVersion);
            if (!w.PutString(ModVersion, WireLimits.MaxModVersionBytes)) return false;
            if (!w.PutString(GameVersion, WireLimits.MaxGameVersionBytes)) return false;
            for (int i = 0; i < WireLimits.CommitHashCount; i++)
            {
                string h = CommitHashes != null && i < CommitHashes.Length ? CommitHashes[i] : string.Empty;
                if (!w.PutString(h, WireLimits.MaxCommitHashBytes)) return false;
            }
            if (!w.PutString(PlayerName, WireLimits.MaxPlayerNameBytes)) return false;
            if (Packs == null || Packs.Length > WireLimits.MaxPackStamps) return false;
            w.Put((ushort)Packs.Length);
            for (int i = 0; i < Packs.Length; i++)
            {
                if (!w.PutString(Packs[i].Uid ?? string.Empty, WireLimits.MaxPackUidBytes)) return false;
                if (!w.PutString(Packs[i].Version ?? string.Empty, WireLimits.MaxPackVersionBytes)) return false;
            }
            w.Put(CampaignId.HasValue);
            if (CampaignId.HasValue) w.PutGuid(CampaignId.Value);
            return true;
        }

        internal override void ReadBody(MsgReader r)
        {
            ProtocolVersion = r.GetInt();
            ModVersion = r.GetString(WireLimits.MaxModVersionBytes);
            GameVersion = r.GetString(WireLimits.MaxGameVersionBytes);
            CommitHashes = new string[WireLimits.CommitHashCount];
            for (int i = 0; i < WireLimits.CommitHashCount; i++)
            {
                CommitHashes[i] = r.GetString(WireLimits.MaxCommitHashBytes);
            }
            PlayerName = r.GetString(WireLimits.MaxPlayerNameBytes);
            int n = r.GetUShort();
            if (n > WireLimits.MaxPackStamps) { r.Fail(); Packs = new PackStamp[0]; }
            else
            {
                Packs = new PackStamp[n];
                for (int i = 0; i < n; i++)
                {
                    Packs[i].Uid = r.GetString(WireLimits.MaxPackUidBytes);
                    Packs[i].Version = r.GetString(WireLimits.MaxPackVersionBytes);
                }
            }
            bool has = r.GetBool();
            CampaignId = has ? r.GetGuid() : (Guid?)null;
        }
    }

    /// <summary>
    /// H->G join answer. On accept the ENVELOPE SessionId is the assigned
    /// session - the guest adopts it (the only message allowed to carry a
    /// non-empty id while the receiver still expects Empty). On reject the
    /// envelope stays Empty, RejectReason is the machine code and Detail is
    /// the human-readable reason ("mod version 0.0.0 != 0.1.0") the guest UI
    /// shows verbatim - the guest cannot reconstruct it, it does not know the
    /// host's versions.
    /// </summary>
    public sealed class HelloAckMsg : NetMessage
    {
        public bool Accepted;
        public HelloRejectReason RejectReason = HelloRejectReason.None;
        public string Detail = string.Empty;    // reject text; "" on accept

        public override MsgType Type => MsgType.HelloAck;

        internal override bool WriteBody(MsgWriter w)
        {
            w.Put(Accepted);
            w.Put((byte)RejectReason);
            return w.PutString(Detail ?? string.Empty, WireLimits.MaxHelloAckDetailBytes);
        }

        internal override void ReadBody(MsgReader r)
        {
            Accepted = r.GetBool();
            byte b = r.GetByte();
            if (b >= 1 && b <= 4) { RejectReason = (HelloRejectReason)b; }
            else if (b == 0) { RejectReason = HelloRejectReason.None; }
            else { r.Fail(); return; }
            Detail = r.GetString(WireLimits.MaxHelloAckDetailBytes);
        }
    }

    /// <summary>
    /// H->G "I am about to stream a save". Payload SessionId restates the
    /// envelope id per spec table - receivers may cross-check equality. Rides
    /// the Save channel so it stays ordered ahead of its SaveChunk stream.
    /// </summary>
    public sealed class SaveOfferMsg : NetMessage
    {
        public Guid OfferSessionId;    // payload copy of envelope SessionId (spec parity)
        public Guid CampaignId;
        public long BaselineSeq;
        public long ByteCount;
        public byte[] Sha256 = new byte[WireLimits.Sha256Bytes];
        /// <summary>
        /// T3.2 review fix (wire change, v1 never shipped): the Campaign
        /// archetype carries no GuidComponent, so CampaignId is Guid.Empty
        /// on real saves - an all-zeros id is useless as the T3.3 guest
        /// filename component. The campaign NAME (SaveGameSystem.
        /// GetCampaignName, the save-group directory identity) is the
        /// practical fallback. T3.3 filename rule: CampaignId when non-empty,
        /// else sanitized CampaignName, else OfferSessionId.
        /// </summary>
        public string CampaignName = string.Empty;

        public override MsgType Type => MsgType.SaveOffer;
        internal override int SizeHint => 96;

        internal override bool WriteBody(MsgWriter w)
        {
            w.PutGuid(OfferSessionId);
            w.PutGuid(CampaignId);
            w.Put(BaselineSeq);
            w.Put(ByteCount);
            if (Sha256 == null || Sha256.Length != WireLimits.Sha256Bytes) return false;
            w.Put(Sha256);
            return w.PutString(CampaignName, WireLimits.MaxCampaignNameBytes);
        }

        internal override void ReadBody(MsgReader r)
        {
            OfferSessionId = r.GetGuid();
            CampaignId = r.GetGuid();
            BaselineSeq = r.GetLong();
            ByteCount = r.GetLong();
            Sha256 = r.GetBytes(WireLimits.Sha256Bytes);
            CampaignName = r.GetString(WireLimits.MaxCampaignNameBytes);
        }
    }

    /// <summary>
    /// H->G one <=64 KiB save chunk. Offset is the byte position in the
    /// (compressed) save stream; the T3.x sender compresses before chunking -
    /// the codec treats Data as opaque.
    /// </summary>
    public sealed class SaveChunkMsg : NetMessage
    {
        public long Offset;
        public byte[] Data = new byte[0];  // length-prefixed, <= WireLimits.MaxChunkBytes

        public override MsgType Type => MsgType.SaveChunk;
        internal override int SizeHint => (Data != null ? Data.Length : 0) + 32;

        internal override bool WriteBody(MsgWriter w)
        {
            w.Put(Offset);
            if (Data == null || Data.Length > WireLimits.MaxChunkBytes) return false;
            w.PutBytesWithLength(Data);
            return true;
        }

        internal override void ReadBody(MsgReader r)
        {
            Offset = r.GetLong();
            Data = r.GetBytesWithLength(WireLimits.MaxChunkBytes);
        }
    }

    /// <summary>G->H "save loaded, world initialized, ready for deltas".</summary>
    public sealed class GuestReadyMsg : NetMessage
    {
        public GuestReadyStatus Status = GuestReadyStatus.Ready;

        public override MsgType Type => MsgType.GuestReady;

        internal override bool WriteBody(MsgWriter w)
        {
            w.Put((byte)Status);
            return true;
        }

        internal override void ReadBody(MsgReader r)
        {
            byte b = r.GetByte();
            if (b >= 1 && b <= 3) { Status = (GuestReadyStatus)b; }
            else { r.Fail(); }
        }
    }

    /// <summary>
    /// Shared body codec for <see cref="StateDeltaMsg"/>/<see cref="PosDeltaMsg"/>:
    /// [seq:int64][hostSendTicks:int64][opCount:uint16][ops]. hostSendTicks is
    /// the host's UTC clock at send time (informational - the guest uses it for
    /// staleness/lag metrics, never for ordering; Seq is the ordering authority).
    /// Kind byte 1..4 = <see cref="DeltaOpKind"/>.
    /// </summary>
    internal static class DeltaOpWire
    {
        internal static bool WriteBody(MsgWriter w, long seq, long hostSendTicks, DeltaOp[] ops)
        {
            w.Put(seq);
            w.Put(hostSendTicks);
            if (ops == null || ops.Length > WireLimits.MaxDeltaOps) return false;
            w.Put((ushort)ops.Length);
            for (int i = 0; i < ops.Length; i++)
            {
                w.Put(ops[i].EntityId);
                byte kind = (byte)ops[i].Kind;
                if (kind < 1 || kind > 4) return false;
                w.Put(kind);
                if (!w.PutString(ops[i].ComponentType ?? string.Empty, WireLimits.MaxComponentTypeBytes)) return false;
                byte[] p = ops[i].Payload;
                if (p == null) { p = new byte[0]; }
                if (p.Length > WireLimits.MaxOpPayloadBytes) return false;
                w.PutBytesWithLength(p);
            }
            return true;
        }

        internal static void ReadBody(MsgReader r, out long seq, out long hostSendTicks, out DeltaOp[] ops)
        {
            seq = r.GetLong();
            hostSendTicks = r.GetLong();
            int n = r.GetUShort();
            if (n > WireLimits.MaxDeltaOps) { r.Fail(); ops = new DeltaOp[0]; return; }
            ops = new DeltaOp[n];
            for (int i = 0; i < n; i++)
            {
                ops[i].EntityId = r.GetInt();
                byte kind = r.GetByte();
                if (kind < 1 || kind > 4) { r.Fail(); ops = new DeltaOp[0]; return; }
                ops[i].Kind = (DeltaOpKind)kind;
                ops[i].ComponentType = r.GetString(WireLimits.MaxComponentTypeBytes);
                ops[i].Payload = r.GetBytesWithLength(WireLimits.MaxOpPayloadBytes);
            }
        }
    }

    /// <summary>
    /// H->G replication batch on the RELIABLE channel. Seq is the host's
    /// monotonic replication counter (first live delta = SaveOffer.BaselineSeq
    /// + 1) - the guest gap-detects on it, so a seq is burned even when encode
    /// or send fails mid-tick (rewinding would silently diverge instead).
    /// Ops are already tick-coalesced by T5.2; the codec only bounds count/size.
    /// </summary>
    public sealed class StateDeltaMsg : NetMessage
    {
        public long Seq;
        public long HostSendTicks;
        public DeltaOp[] Ops = new DeltaOp[0];

        public override MsgType Type => MsgType.StateDelta;
        internal override int SizeHint => 256;

        internal override bool WriteBody(MsgWriter w)
        {
            return DeltaOpWire.WriteBody(w, Seq, HostSendTicks, Ops);
        }

        internal override void ReadBody(MsgReader r)
        {
            DeltaOpWire.ReadBody(r, out Seq, out HostSendTicks, out Ops);
        }
    }

    /// <summary>
    /// H->G latest-state batch (T5.2): client-derivable/positional component
    /// snapshots (Position/Velocity/CampaignPlayTime/GeoscapeDateTime) sent at
    /// ~1-2 Hz on <see cref="Protocol.ChannelPos"/> with Sequenced delivery -
    /// a dropped packet is superseded by the next tick, so it never resends
    /// and never stalls the reliable channel. Seq is a SEPARATE counter from
    /// <see cref="StateDeltaMsg.Seq"/> (latest-state seq space); the guest must
    /// NOT gap-flag it against reliable seqs. Ops use the same DeltaOp shape
    /// (SetComponent only in practice).
    /// </summary>
    public sealed class PosDeltaMsg : NetMessage
    {
        public long Seq;
        public long HostSendTicks;
        public DeltaOp[] Ops = new DeltaOp[0];

        public override MsgType Type => MsgType.PosDelta;
        internal override int SizeHint => 256;

        internal override bool WriteBody(MsgWriter w)
        {
            return DeltaOpWire.WriteBody(w, Seq, HostSendTicks, Ops);
        }

        internal override void ReadBody(MsgReader r)
        {
            DeltaOpWire.ReadBody(r, out Seq, out HostSendTicks, out Ops);
        }
    }

    /// <summary>G->H resync request; the host answers with a fresh SaveOffer.</summary>
    public sealed class ResyncReqMsg : NetMessage
    {
        public ResyncReason Reason;
        public string Detail = string.Empty;   // diagnostic only; never shown to peers

        public override MsgType Type => MsgType.ResyncReq;

        internal override bool WriteBody(MsgWriter w)
        {
            byte b = (byte)Reason;
            if (b < 1 || b > 5) return false;
            w.Put(b);
            return w.PutString(Detail ?? string.Empty, WireLimits.MaxResyncDetailBytes);
        }

        internal override void ReadBody(MsgReader r)
        {
            byte b = r.GetByte();
            if (b >= 1 && b <= 5) { Reason = (ResyncReason)b; }
            else { r.Fail(); return; }
            Detail = r.GetString(WireLimits.MaxResyncDetailBytes);
        }
    }

    /// <summary>Both directions: session-state notifications.</summary>
    public sealed class SessionNoticeMsg : NetMessage
    {
        public SessionNoticeKind Notice;

        public override MsgType Type => MsgType.SessionNotice;

        internal override bool WriteBody(MsgWriter w)
        {
            byte b = (byte)Notice;
            if (b < 1 || b > 7) return false;
            w.Put(b);
            return true;
        }

        internal override void ReadBody(MsgReader r)
        {
            byte b = r.GetByte();
            if (b >= 1 && b <= 7) { Notice = (SessionNoticeKind)b; }
            else { r.Fail(); }
        }
    }

    /// <summary>2 s keepalive. Ticks = sender UTC ticks; Pong echoes it.</summary>
    public sealed class PingMsg : NetMessage
    {
        public long Ticks;

        public override MsgType Type => MsgType.Ping;

        internal override bool WriteBody(MsgWriter w) { w.Put(Ticks); return true; }
        internal override void ReadBody(MsgReader r) { Ticks = r.GetLong(); }
    }

    /// <summary>Reply to Ping; Ticks is the echoed value (RTT = now - Ticks).</summary>
    public sealed class PongMsg : NetMessage
    {
        public long Ticks;

        public override MsgType Type => MsgType.Pong;

        internal override bool WriteBody(MsgWriter w) { w.Put(Ticks); return true; }
        internal override void ReadBody(MsgReader r) { Ticks = r.GetLong(); }
    }

    /// <summary>T5.5 H->G: "compute your world hash exactly at applied seq".</summary>
    public sealed class HashReqMsg : NetMessage
    {
        public long Seq;

        public override MsgType Type => MsgType.HashReq;

        internal override bool WriteBody(MsgWriter w) { w.Put(Seq); return true; }
        internal override void ReadBody(MsgReader r) { Seq = r.GetLong(); }
    }

    /// <summary>
    /// T5.5 G->H: world hash at HashReq.Seq. Count = number of hashed
    /// entity/component items - a cheap divergence-triage signal alongside Hash.
    /// </summary>
    public sealed class HashRespMsg : NetMessage
    {
        public long Seq;
        public byte[] Hash = new byte[WireLimits.Sha256Bytes];
        public int Count;

        public override MsgType Type => MsgType.HashResp;
        internal override int SizeHint => 64;

        internal override bool WriteBody(MsgWriter w)
        {
            w.Put(Seq);
            if (Hash == null || Hash.Length != WireLimits.Sha256Bytes) return false;
            w.Put(Hash);
            w.Put(Count);
            return true;
        }

        internal override void ReadBody(MsgReader r)
        {
            Seq = r.GetLong();
            Hash = r.GetBytes(WireLimits.Sha256Bytes);
            Count = r.GetInt();
        }
    }
}
