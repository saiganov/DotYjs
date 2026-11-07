namespace DotYjs.Benchmarks;

/// <summary>Builds deterministic documents shared by the benchmarks.</summary>
public static class DocumentFactory
{
    public const string TextName = "text";

    /// <summary>
    /// Simulates <paramref name="edits"/> keystrokes by two clients that edit the same text and sync after
    /// every edit: mostly inserts at a random position, with every fifth edit deleting a short range.
    /// Every edit is its own transaction, as in a real editor.
    /// </summary>
    public static Doc CreateEditedDocument(int edits, int seed = 42) =>
        CreateEditedDocument(edits, seed, updates: null);

    /// <summary>Same as <see cref="CreateEditedDocument(int, int)"/>, also collecting the v1 update of every transaction.</summary>
    public static Doc CreateEditedDocument(int edits, int seed, List<byte[]>? updates)
    {
        var random = new Random(seed);
        var a = new Doc { ClientID = 1 };
        var b = new Doc { ClientID = 2 };
        // collect only the local updates of each client, not the copies the other client re-emits when applying them
        a.Update += (update, origin, _, _) =>
        {
            if (origin == b) return;
            updates?.Add(update);
            UpdateCodec.ApplyUpdate(b, update, a);
        };
        b.Update += (update, origin, _, _) =>
        {
            if (origin == a) return;
            updates?.Add(update);
            UpdateCodec.ApplyUpdate(a, update, b);
        };

        for (var i = 0; i < edits; i++)
        {
            var text = (i % 2 == 0 ? a : b).GetText(TextName);
            var length = text.Length;
            if (i % 5 == 4 && length > 0)
            {
                var index = random.Next(length);
                text.Delete(index, Math.Min(1 + random.Next(4), length - index));
            }
            else
            {
                text.Insert(random.Next(length + 1), ((char)('a' + random.Next(26))).ToString());
            }
        }

        b.Destroy();
        return a;
    }
}
