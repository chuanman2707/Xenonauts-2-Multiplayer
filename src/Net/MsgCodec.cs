using System;
using System.Collections.Generic;
using System.Text;
using FlyingWormConsole3.LiteNetLib;
using FlyingWormConsole3.LiteNetLib.Utils;

namespace X2Coop.Net
{
    /// <summary>
    /// Bounds-checking reader over NetDataReader. Every primitive read verifies
    /// AvailableBytes first; every length prefix is validated BEFORE the
    /// allocation it would drive. Any violation sets <see cref="Failed"/> and
    /// returns a safe default - callers never see an exception, and the codec
    /// drops the message (the wire format is fixed, so a bound violation is
    /// always corruption or hostile input).
    ///
    /// Two extra rules the raw reader does not give us:
    ///  - string lengths are capped per field (see <see cref="WireLimits"/>)
    ///  - after the body, the codec requires EndOfData - trailing garbage drops
    /// </summary>
    internal sealed class MsgReader
    {
        private readonly NetDataReader _r;
        public bool Failed { get; private set; }

        public MsgReader(NetDataReader r) { _r = r; }

        public bool EndOfData => _r.EndOfData;

        public void Fail() { Failed = true; }

        public byte GetByte()
        {
            if (Failed || _r.AvailableBytes < 1) { Failed = true; return 0; }
            return _r.GetByte();
        }

        public bool GetBool() { return GetByte() != 0; }

        public ushort GetUShort()
        {
            if (Failed || _r.AvailableBytes < 2) { Failed = true; return 0; }
            return _r.GetUShort();
        }

        public int GetInt()
        {
            if (Failed || _r.AvailableBytes < 4) { Failed = true; return 0; }
            return _r.GetInt();
        }

        public long GetLong()
        {
            if (Failed || _r.AvailableBytes < 8) { Failed = true; return 0; }
            return _r.GetLong();
        }

        /// <summary>Exactly <paramref name="count"/> raw bytes (hashes, ids).</summary>
        public byte[] GetBytes(int count)
        {
            if (Failed || _r.AvailableBytes < count)
            {
                Failed = true;
                return new byte[count];
            }
            byte[] buf = new byte[count];
            _r.GetBytes(buf, count);
            return buf;
        }

        /// <summary>
        /// int32-length-prefixed byte blob, same layout as
        /// NetDataWriter.PutBytesWithLength. Length must be 0..maxBytes and fit
        /// in the remaining buffer before the array is allocated.
        /// </summary>
        public byte[] GetBytesWithLength(int maxBytes)
        {
            int n = GetInt();
            if (Failed) { return new byte[0]; }
            if (n < 0 || n > maxBytes || _r.AvailableBytes < n)
            {
                Failed = true;
                return new byte[0];
            }
            byte[] buf = new byte[n];
            if (n > 0) { _r.GetBytes(buf, n); }
            return buf;
        }

        /// <summary>
        /// int32-bytecount + UTF8, the exact layout NetDataWriter.Put(string)
        /// writes - but bounded: byteCount must be 0..maxBytes and fit the
        /// buffer before decoding. A malformed count fails the message rather
        /// than silently returning empty/misaligned data like GetString can.
        /// </summary>
        public string GetString(int maxBytes)
        {
            int n = GetInt();
            if (Failed) { return string.Empty; }
            if (n < 0 || n > maxBytes || _r.AvailableBytes < n)
            {
                Failed = true;
                return string.Empty;
            }
            if (n == 0) { return string.Empty; }
            byte[] buf = new byte[n];
            _r.GetBytes(buf, n);
            try
            {
                // Strict UTF8: throw on invalid sequences so corrupted bytes
                // fail the message instead of becoming silent U+FFFD.
                return new UTF8Encoding(false, true).GetString(buf);
            }
            catch
            {
                Failed = true;
                return string.Empty;
            }
        }

        /// <summary>16-byte sessionId/campaignId (Guid.ToByteArray layout).</summary>
        public Guid GetGuid()
        {
            return new Guid(GetBytes(WireLimits.SessionIdBytes));
        }
    }

    /// <summary>
    /// Bounds-checking writer over NetDataWriter. Puts are infallible except
    /// <see cref="PutString"/>/<see cref="PutBytesWithLength"/> which return
    /// false when a field exceeds its wire cap - a caller bug, not an attack,
    /// so Encode surfaces it as a logged failure instead of truncating
    /// silently (truncated pack uids or type names would corrupt semantics).
    /// </summary>
    internal sealed class MsgWriter
    {
        private readonly NetDataWriter _w;

        public MsgWriter(NetDataWriter w) { _w = w; }

        public void Put(byte v) { _w.Put(v); }
        public void Put(bool v) { _w.Put(v); }
        public void Put(ushort v) { _w.Put(v); }
        public void Put(int v) { _w.Put(v); }
        public void Put(long v) { _w.Put(v); }
        public void Put(byte[] v) { _w.Put(v); }
        public void PutBytesWithLength(byte[] v) { _w.PutBytesWithLength(v); }

        /// <summary>Same layout as NetDataWriter.Put(string): int32 UTF8 byte count + bytes.</summary>
        public bool PutString(string value, int maxBytes)
        {
            int n = string.IsNullOrEmpty(value) ? 0 : Encoding.UTF8.GetByteCount(value);
            if (n > maxBytes) { return false; }
            _w.Put(value ?? string.Empty);
            return true;
        }

        public void PutGuid(Guid g)
        {
            _w.Put(g.ToByteArray()); // 16 bytes, .NET mixed-endian - symmetric both ways
        }
    }

    /// <summary>
    /// Protocol v1 codec. Envelope = [MsgType:1][sessionId:16][payload].
    ///
    /// Decode safety model:
    ///  - size gate:   EnvelopeSize..MaxMessageSize (transport also gates, the
    ///                 codec repeats it because the self-test feeds it raw bytes)
    ///  - MsgType whitelist: unknown values drop + log
    ///  - sessionId:   must equal the expected session id. Two pre-session
    ///                 exceptions: Hello (always Empty - it is what starts a
    ///                 session) and HelloAck while we still expect Empty (its
    ///                 envelope carries the host's assignment)
    ///  - payload:     all reads go through <see cref="MsgReader"/>; a bound
    ///                 violation or trailing bytes drop + log
    ///  - nothing throws: Encode failure returns null, decode failure returns
    ///                 false with a <see cref="DropReason"/> - all logged Warn
    ///
    /// Channel/delivery policy is codec-owned (<see cref="ChannelFor"/> /
    /// <see cref="DeliveryFor"/>) so a sender can never put a message on the
    /// wrong channel. Everything rides ReliableOrdered EXCEPT PosDelta
    /// (Sequenced - the T5.2 R9 latest-state lane; a dropped packet is stale
    /// by definition). Structural StateDelta ops are never Sequenced.
    /// </summary>
    public static class MsgCodec
    {
        /// <summary>[type:1] + [sessionId:16].</summary>
        public const int EnvelopeSize = 1 + WireLimits.SessionIdBytes;

        /// <summary>Why TryDecode refused a buffer.</summary>
        public enum DropReason : byte
        {
            None = 0,
            ShortBuffer = 1,     // < EnvelopeSize
            Oversize = 2,        // > MaxMessageSize
            UnknownType = 3,     // MsgType byte not in the whitelist
            SessionMismatch = 4, // envelope sessionId != expected
            Malformed = 5,       // bound violation mid-payload
            TrailingBytes = 6,   // payload did not consume the whole message
        }

        // Registry of implemented types (all 12 spec-section-6 + T5.5 messages).
        private static readonly Dictionary<MsgType, Func<NetMessage>> Factories =
            new Dictionary<MsgType, Func<NetMessage>>
            {
                { MsgType.Hello, () => new HelloMsg() },
                { MsgType.HelloAck, () => new HelloAckMsg() },
                { MsgType.SaveOffer, () => new SaveOfferMsg() },
                { MsgType.SaveChunk, () => new SaveChunkMsg() },
                { MsgType.GuestReady, () => new GuestReadyMsg() },
                { MsgType.StateDelta, () => new StateDeltaMsg() },
                { MsgType.ResyncReq, () => new ResyncReqMsg() },
                { MsgType.SessionNotice, () => new SessionNoticeMsg() },
                { MsgType.Ping, () => new PingMsg() },
                { MsgType.Pong, () => new PongMsg() },
                { MsgType.PosDelta, () => new PosDeltaMsg() },
                { MsgType.HashReq, () => new HashReqMsg() },
                { MsgType.HashResp, () => new HashRespMsg() },
            };

        /// <summary>Number of registered message types (the "n" in "codec ok n/n").</summary>
        public static int RegisteredCount => Factories.Count;

        /// <summary>All registered MsgTypes (self-test iteration order).</summary>
        public static IEnumerable<MsgType> RegisteredTypes => Factories.Keys;

        /// <summary>
        /// Save stream rides its own channel so chunks can't head-of-line
        /// block control/deltas. T5.2: PosDelta latest-state traffic gets its
        /// own channel (<see cref="Protocol.ChannelPos"/>) - Sequenced packets
        /// carry no resend semantics, so a burst of position/clocks can never
        /// stall the reliable delta or save streams.
        /// </summary>
        public static byte ChannelFor(MsgType type)
        {
            if (type == MsgType.SaveOffer || type == MsgType.SaveChunk)
            {
                return Protocol.ChannelSave;
            }
            if (type == MsgType.PosDelta)
            {
                return Protocol.ChannelPos;
            }
            return Protocol.ChannelControl;
        }

        /// <summary>
        /// Spec section 4: control, save and STRUCTURAL deltas are
        /// ReliableOrdered (T5.2 R9 addendum carves out the exception):
        /// PosDelta's client-derivable latest-state values ride Sequenced -
        /// each new tick supersedes the previous value wholesale, so a lost
        /// packet is stale by definition and LiteNetLib drops it instead of
        /// stalling the channel for a resend. StateDelta stays reliable: a
        /// lost structural op (create/delete/remove) never heals.
        /// </summary>
        public static DeliveryMethod DeliveryFor(MsgType type)
        {
            return type == MsgType.PosDelta
                ? DeliveryMethod.Sequenced
                : DeliveryMethod.ReliableOrdered;
        }

        /// <summary>
        /// Encode a message to a fresh byte[]. Returns null (and logs) when a
        /// field violates its bound or the result exceeds MaxMessageSize.
        /// </summary>
        public static byte[] Encode(NetMessage msg)
        {
            if (msg == null) { return null; }
            var w = new NetDataWriter(true, msg.SizeHint);
            var mw = new MsgWriter(w);
            w.Put((byte)msg.Type);
            mw.PutGuid(msg.SessionId);
            if (!msg.WriteBody(mw))
            {
                Log.Warn("codec encode failed: " + msg.Type + " field over bound");
                return null;
            }
            if (w.Length > Protocol.MaxMessageSize)
            {
                Log.Warn("codec encode failed: " + msg.Type + " len=" + w.Length
                    + " > max=" + Protocol.MaxMessageSize);
                return null;
            }
            return w.CopyData();
        }

        /// <summary>Envelope+payload decode; see class doc for the drop rules.</summary>
        public static bool TryDecode(byte[] data, Guid expectedSessionId, out NetMessage msg)
        {
            return TryDecode(data, 0, data != null ? data.Length : 0,
                expectedSessionId, out msg, out _);
        }

        /// <summary>Full decode with the drop reason surfaced for tests/diagnostics.</summary>
        public static bool TryDecode(byte[] data, int offset, int length,
            Guid expectedSessionId, out NetMessage msg, out DropReason drop)
        {
            msg = null;
            drop = DropReason.None;

            if (data == null || length < EnvelopeSize || offset < 0 || length > data.Length - offset)
            {
                drop = DropReason.ShortBuffer;
                Log.Warn("codec drop: short buffer len=" + length);
                return false;
            }
            if (length > Protocol.MaxMessageSize)
            {
                drop = DropReason.Oversize;
                Log.Warn("codec drop: oversize len=" + length);
                return false;
            }

            byte typeByte = data[offset];
            MsgType type = (MsgType)typeByte;
            if (!Factories.TryGetValue(type, out Func<NetMessage> factory))
            {
                drop = DropReason.UnknownType;
                Log.Warn("codec drop: unknown msg-type " + typeByte);
                return false;
            }

            var reader = new NetDataReader(data, offset, offset + length);
            var mr = new MsgReader(reader);
            reader.GetByte(); // type byte (known-good: whitelist already checked)
            Guid sessionId = mr.GetGuid();
            if (mr.Failed)
            {
                drop = DropReason.Malformed;
                Log.Warn("codec drop: " + type + " bad sessionId field");
                return false;
            }

            if (sessionId != expectedSessionId && !IsPreSessionAllowed(type, sessionId, expectedSessionId))
            {
                drop = DropReason.SessionMismatch;
                Log.Warn("codec drop: " + type + " session=" + ShortId(sessionId)
                    + " expected=" + ShortId(expectedSessionId));
                return false;
            }

            NetMessage m = factory();
            m.SessionId = sessionId;
            try
            {
                m.ReadBody(mr);
            }
            catch (Exception ex)
            {
                // MsgReader is designed never to throw; this is belt-and-braces
                // for future fields (e.g. a Guid ctor edge) - still a drop.
                drop = DropReason.Malformed;
                Log.Warn("codec drop: " + type + " threw " + ex.GetType().Name);
                return false;
            }
            if (mr.Failed)
            {
                drop = DropReason.Malformed;
                Log.Warn("codec drop: " + type + " malformed payload");
                return false;
            }
            if (!reader.EndOfData)
            {
                drop = DropReason.TrailingBytes;
                Log.Warn("codec drop: " + type + " trailing=" + reader.AvailableBytes + "B");
                return false;
            }
            msg = m;
            return true;
        }

        /// <summary>
        /// The two pre-session exceptions: Hello always carries Empty (it is
        /// what opens a session); HelloAck may arrive with the assigned id while
        /// the guest still expects Empty. Every other combination must match.
        /// </summary>
        private static bool IsPreSessionAllowed(MsgType type, Guid sessionId, Guid expected)
        {
            if (type == MsgType.Hello && sessionId == Guid.Empty)
            {
                return true;
            }
            if (type == MsgType.HelloAck && expected == Guid.Empty)
            {
                return true;
            }
            return false;
        }

        /// <summary>First 4 bytes of a sessionId as hex - enough to correlate, short for logs.</summary>
        internal static string ShortId(Guid id)
        {
            if (id == Guid.Empty) { return "none"; }
            byte[] b = id.ToByteArray();
            var sb = new StringBuilder(8);
            for (int i = 0; i < 4; i++) { sb.Append(b[i].ToString("x2")); }
            return sb.ToString();
        }
    }
}
