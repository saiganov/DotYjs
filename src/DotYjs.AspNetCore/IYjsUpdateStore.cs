namespace DotYjs.AspNetCore;

/// <summary>A stored update and its row id.</summary>
public readonly record struct StoredUpdate(long Id, byte[] Update);

/// <summary>
/// Append-only storage of document updates, the only thing a database adapter has to implement to persist documents.
/// <see cref="UpdateLogPersistence"/> turns it into an <see cref="IYjsPersistence"/>: it loads and merges the stored
/// updates when a document is bound, appends every incremental update and compacts the rows it has seen into a single
/// full-state row. All updates are in the Yjs v1 update format; the store treats them as opaque bytes.
/// Row ids must be unique per document and increase in insertion order (an auto-increment key).
/// </summary>
public interface IYjsUpdateStore
{
    /// <summary>Returns all stored updates of a document ordered by id (empty when the document is unknown).</summary>
    Task<IReadOnlyList<StoredUpdate>> LoadAsync(string docName, CancellationToken cancellationToken);

    /// <summary>Appends one update to the document's log and returns its row id.</summary>
    Task<long> AppendAsync(string docName, byte[] update, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically deletes the rows with the given ids and inserts <paramref name="state"/> (which contains them merged);
    /// returns the id of the new row. Rows not listed, e.g. appended concurrently by another server node, must stay.
    /// </summary>
    Task<long> CompactAsync(string docName, byte[] state, IReadOnlyCollection<long> replacedIds, CancellationToken cancellationToken);
}
