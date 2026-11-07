using DotYjs.Lib0;

namespace DotYjs.Protocols;

/// <summary>
/// The message envelope used by <c>y-websocket</c> (client and server): every WebSocket frame starts with a
/// varuint message type followed by the payload of the respective sub-protocol.
/// </summary>
public static class YWebSocketProtocol
{
    /// <summary>Payload is a <see cref="SyncProtocol"/> message.</summary>
    public const int MessageSync = 0;

    /// <summary>Payload is a varuint8array containing an <see cref="Awareness"/> update.</summary>
    public const int MessageAwareness = 1;

    /// <summary>Payload is an auth message (varuint 0 = permission denied + varstring reason).</summary>
    public const int MessageAuth = 2;

    /// <summary>Request the receiver to send its awareness states.</summary>
    public const int MessageQueryAwareness = 3;

    /// <summary>Builds a <c>[MessageSync, syncStep1]</c> frame.</summary>
    public static byte[] EncodeSyncStep1(Doc doc)
    {
        var e = new Encoder();
        e.WriteVarUint(MessageSync);
        SyncProtocol.WriteSyncStep1(e, doc);
        return e.ToArray();
    }

    /// <summary>Builds a <c>[MessageSync, syncStep2]</c> frame.</summary>
    public static byte[] EncodeSyncStep2(Doc doc, byte[]? encodedStateVector = null)
    {
        var e = new Encoder();
        e.WriteVarUint(MessageSync);
        SyncProtocol.WriteSyncStep2(e, doc, encodedStateVector);
        return e.ToArray();
    }

    /// <summary>Builds a <c>[MessageSync, update]</c> frame.</summary>
    public static byte[] EncodeUpdate(byte[] update)
    {
        var e = new Encoder();
        e.WriteVarUint(MessageSync);
        SyncProtocol.WriteUpdate(e, update);
        return e.ToArray();
    }

    /// <summary>Builds a <c>[MessageAwareness, awarenessUpdate]</c> frame.</summary>
    public static byte[] EncodeAwareness(byte[] awarenessUpdate)
    {
        var e = new Encoder();
        e.WriteVarUint(MessageAwareness);
        e.WriteVarUint8Array(awarenessUpdate);
        return e.ToArray();
    }

    /// <summary>Builds a <c>[MessageQueryAwareness]</c> frame.</summary>
    public static byte[] EncodeQueryAwareness()
    {
        var e = new Encoder();
        e.WriteVarUint(MessageQueryAwareness);
        return e.ToArray();
    }

    /// <summary>Builds a <c>[MessageAuth, permissionDenied, reason]</c> frame (y-protocols/auth).</summary>
    public static byte[] EncodePermissionDenied(string reason)
    {
        var e = new Encoder();
        e.WriteVarUint(MessageAuth);
        e.WriteVarUint(0);
        e.WriteVarString(reason);
        return e.ToArray();
    }
}
