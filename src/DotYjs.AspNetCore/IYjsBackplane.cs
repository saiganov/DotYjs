namespace DotYjs.AspNetCore;

public enum YjsBackplaneMessageKind
{
    /// <summary>Payload is a Yjs document update (v1).</summary>
    DocumentUpdate = 0,

    /// <summary>Payload is an awareness update (y-protocols encoding).</summary>
    AwarenessUpdate = 1,

    /// <summary>
    /// Payload is the sender's state vector. Sent when a node loads a document; other nodes answer with a
    /// <see cref="SyncResponse"/> and an <see cref="AwarenessUpdate"/> with all their states.
    /// </summary>
    SyncRequest = 2,

    /// <summary>
    /// Answer to a <see cref="SyncRequest"/>: varuint8array update (what the requester is missing) followed by the
    /// responder's state vector, so the requester can publish what the responder is missing in turn.
    /// </summary>
    SyncResponse = 3
}

/// <summary>A message exchanged between server nodes hosting the same document.</summary>
public sealed class YjsBackplaneMessage
{
    public YjsBackplaneMessage(YjsBackplaneMessageKind kind, byte[] payload, string nodeId)
    {
        Kind = kind;
        Payload = payload;
        NodeId = nodeId;
    }

    public YjsBackplaneMessageKind Kind { get; }
    public byte[] Payload { get; }

    /// <summary>Id of the node that produced the message (so a node can ignore its own messages).</summary>
    public string NodeId { get; }

    /// <summary>Compact binary form: varuint kind, varstring nodeId, varuint8array payload.</summary>
    public byte[] Encode()
    {
        var e = new Lib0.Encoder();
        e.WriteVarUint((long)Kind);
        e.WriteVarString(NodeId);
        e.WriteVarUint8Array(Payload);
        return e.ToArray();
    }

    public static YjsBackplaneMessage Decode(byte[] data)
    {
        var d = new Lib0.Decoder(data);
        var kind = (YjsBackplaneMessageKind)d.ReadVarUint();
        var nodeId = d.ReadVarString();
        var payload = d.ReadVarUint8Array();
        return new YjsBackplaneMessage(kind, payload, nodeId);
    }
}

/// <summary>
/// Pub/sub channel between server nodes, so several ASP.NET Core instances can host the same document:
/// every local document/awareness change is published, and changes from other nodes are applied locally and
/// forwarded to this node's clients. Register an implementation (e.g. Redis) as a singleton; without one the
/// server works as a single node.
/// </summary>
public interface IYjsBackplane
{
    /// <summary>Publishes a message for all nodes hosting <paramref name="docName"/> (including this node; receivers filter by <see cref="YjsBackplaneMessage.NodeId"/>).</summary>
    Task PublishAsync(string docName, YjsBackplaneMessage message, CancellationToken cancellationToken = default);

    /// <summary>Subscribes to messages for <paramref name="docName"/>. Dispose the result to unsubscribe.</summary>
    Task<IAsyncDisposable> SubscribeAsync(string docName, Func<YjsBackplaneMessage, Task> handler, CancellationToken cancellationToken = default);
}
