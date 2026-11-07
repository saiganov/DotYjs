using DotYjs.Lib0;

namespace DotYjs.Protocols;

/// <summary>
/// Port of <c>y-protocols/sync</c>. Wire-compatible with JS <c>y-protocols</c> 1.x.
/// <para>
/// Core Yjs defines two message types:
/// <list type="bullet">
/// <item>YjsSyncStep1: includes the state vector of the sending client. When received, the client replies with YjsSyncStep2.</item>
/// <item>YjsSyncStep2: includes all missing structs and the complete delete set. When received, the client is assured
/// that it received all information from the remote client.</item>
/// </list>
/// A update message (YjsUpdate) carries an incremental document update.
/// </para>
/// </summary>
public static class SyncProtocol
{
    public const int MessageYjsSyncStep1 = 0;
    public const int MessageYjsSyncStep2 = 1;
    public const int MessageYjsUpdate = 2;

    /// <summary>Create a sync step 1 message based on the state of the current shared document.</summary>
    public static void WriteSyncStep1(Encoder encoder, Doc doc)
    {
        encoder.WriteVarUint(MessageYjsSyncStep1);
        encoder.WriteVarUint8Array(UpdateCodec.EncodeStateVector(doc));
    }

    public static void WriteSyncStep2(Encoder encoder, Doc doc, byte[]? encodedStateVector = null)
    {
        encoder.WriteVarUint(MessageYjsSyncStep2);
        encoder.WriteVarUint8Array(UpdateCodec.EncodeStateAsUpdate(doc, encodedStateVector));
    }

    /// <summary>Read SyncStep1 message and reply with SyncStep2.</summary>
    public static void ReadSyncStep1(Decoder decoder, Encoder encoder, Doc doc)
        => WriteSyncStep2(encoder, doc, decoder.ReadVarUint8Array());

    /// <summary>Read and apply Structs and then DeleteStore to a Yjs document.</summary>
    public static void ReadSyncStep2(Decoder decoder, Doc doc, object? transactionOrigin = null)
        => UpdateCodec.ApplyUpdate(doc, decoder.ReadVarUint8Array(), transactionOrigin);

    public static void WriteUpdate(Encoder encoder, byte[] update)
    {
        encoder.WriteVarUint(MessageYjsUpdate);
        encoder.WriteVarUint8Array(update);
    }

    /// <summary>Read and apply an update message.</summary>
    public static void ReadUpdate(Decoder decoder, Doc doc, object? transactionOrigin = null)
        => ReadSyncStep2(decoder, doc, transactionOrigin);

    /// <summary>
    /// Reads a sync message from <paramref name="decoder"/>, applies it to <paramref name="doc"/> and writes the
    /// reply (if any) to <paramref name="encoder"/>. Returns the message type that was read.
    /// </summary>
    public static int ReadSyncMessage(Decoder decoder, Encoder encoder, Doc doc, object? transactionOrigin = null)
    {
        var messageType = (int)decoder.ReadVarUint();
        switch (messageType)
        {
            case MessageYjsSyncStep1: ReadSyncStep1(decoder, encoder, doc); break;
            case MessageYjsSyncStep2: ReadSyncStep2(decoder, doc, transactionOrigin); break;
            case MessageYjsUpdate: ReadUpdate(decoder, doc, transactionOrigin); break;
            default: throw new InvalidDataException($"Unknown sync message type {messageType}");
        }
        return messageType;
    }

    // ---- convenience helpers producing whole messages ----

    public static byte[] EncodeSyncStep1(Doc doc)
    {
        var e = new Encoder();
        WriteSyncStep1(e, doc);
        return e.ToArray();
    }

    public static byte[] EncodeSyncStep2(Doc doc, byte[]? encodedStateVector = null)
    {
        var e = new Encoder();
        WriteSyncStep2(e, doc, encodedStateVector);
        return e.ToArray();
    }

    public static byte[] EncodeUpdate(byte[] update)
    {
        var e = new Encoder();
        WriteUpdate(e, update);
        return e.ToArray();
    }
}
