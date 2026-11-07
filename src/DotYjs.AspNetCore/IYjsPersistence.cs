namespace DotYjs.AspNetCore;

/// <summary>
/// Persistence hook, mirroring y-websocket's <c>setPersistence({ bindState, writeState })</c>.
/// Implement it to load documents from a database and store updates.
/// </summary>
public interface IYjsPersistence
{
    /// <summary>
    /// Called once when a document is loaded into memory. Typically applies the stored state to <paramref name="doc"/>
    /// (<see cref="UpdateCodec.ApplyUpdate(Doc, byte[], object?)"/>) and subscribes to <see cref="Doc.Update"/> to store
    /// incremental updates. The document is not shared with clients before this task completes.
    /// </summary>
    Task BindStateAsync(string docName, Doc doc, CancellationToken cancellationToken);

    /// <summary>Called before a document is unloaded (last connection closed). Store the full state here if needed.</summary>
    Task WriteStateAsync(string docName, Doc doc, CancellationToken cancellationToken);
}
