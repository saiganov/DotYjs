namespace DotYjs.EntityFrameworkCore;

/// <summary>One stored Yjs update (or a compacted full state) of a document. Table <c>YjsUpdates</c>.</summary>
public class YjsUpdateEntity
{
    /// <summary>Row id; increases with every appended update.</summary>
    public long Id { get; set; }

    /// <summary>Name of the document (the y-websocket room).</summary>
    public string DocName { get; set; } = "";

    /// <summary>Yjs update in the v1 format.</summary>
    public byte[] Update { get; set; } = Array.Empty<byte>();

    /// <summary>When the row was written (UTC).</summary>
    public DateTime CreatedUtc { get; set; }
}
