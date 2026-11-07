using DotYjs.Lib0;

namespace DotYjs;

/// <summary>A snapshot of a document: its delete set and state vector at some point in time.</summary>
public sealed class Snapshot
{
    public Snapshot(DeleteSet ds, Dictionary<long, long> sv)
    {
        Ds = ds;
        Sv = sv;
    }

    /// <summary>The delete set.</summary>
    public DeleteSet Ds { get; }

    /// <summary>The state vector.</summary>
    public Dictionary<long, long> Sv { get; }

    public static Snapshot Empty => new(new DeleteSet(), new Dictionary<long, long>());

    public static bool Equal(Snapshot snap1, Snapshot snap2)
    {
        var sv1 = snap1.Sv;
        var sv2 = snap2.Sv;
        if (sv1.Count != sv2.Count || snap1.Ds.Clients.Count != snap2.Ds.Clients.Count) return false;
        foreach (var (key, value) in sv1)
        {
            if (sv2.GetValueOrDefault(key, -1) != value) return false;
        }
        return DeleteSet.Equal(snap1.Ds, snap2.Ds);
    }

    public byte[] EncodeV2(IDSEncoder? encoder = null)
    {
        encoder ??= new DSEncoderV2();
        DeleteSet.Write(encoder, Ds);
        UpdateCodec.WriteStateVector(encoder, Sv);
        return encoder.ToArray();
    }

    public byte[] Encode() => EncodeV2(new DSEncoderV1());

    public static Snapshot DecodeV2(byte[] buf, IDSDecoder? decoder = null)
    {
        decoder ??= new DSDecoderV2(new Decoder(buf));
        return new Snapshot(DeleteSet.Read(decoder), UpdateCodec.ReadStateVector(decoder));
    }

    public static Snapshot Decode(byte[] buf) => DecodeV2(buf, new DSDecoderV1(new Decoder(buf)));

    /// <summary>Creates a snapshot of the current state of a document.</summary>
    public static Snapshot FromDoc(Doc doc) => new(DeleteSet.FromStructStore(doc.Store), doc.Store.GetStateVector());

    internal static bool IsVisible(Item item, Snapshot? snapshot) => snapshot == null
        ? !item.Deleted
        : snapshot.Sv.ContainsKey(item.Id.Client) && snapshot.Sv.GetValueOrDefault(item.Id.Client) > item.Id.Clock && !snapshot.Ds.IsDeleted(item.Id);

    private static readonly object SplitMetaKey = new();

    internal static void SplitSnapshotAffectedStructs(Transaction transaction, Snapshot snapshot)
    {
        if (!transaction.Meta.TryGetValue(SplitMetaKey, out var metaObj) || metaObj is not HashSet<Snapshot> meta)
        {
            meta = new HashSet<Snapshot>();
            transaction.Meta[SplitMetaKey] = meta;
        }
        var store = transaction.Doc.Store;
        // check if we already split for this snapshot
        if (!meta.Contains(snapshot))
        {
            foreach (var (client, clock) in snapshot.Sv)
            {
                if (clock < store.GetState(client)) store.GetItemCleanStart(transaction, new ID(client, clock));
            }
            DeleteSet.IterateDeletedStructs(transaction, snapshot.Ds, _ => { });
            meta.Add(snapshot);
        }
    }

    /// <summary>Creates a new document that contains only the content visible in the snapshot. <paramref name="originDoc"/> must have GC disabled.</summary>
    public static Doc CreateDocFromSnapshot(Doc originDoc, Snapshot snapshot, Doc? newDoc = null)
    {
        if (originDoc.Gc) throw new InvalidOperationException("Garbage-collection must be disabled in `originDoc`!");
        newDoc ??= new Doc();
        var sv = snapshot.Sv;
        var ds = snapshot.Ds;
        var encoder = new UpdateEncoderV2();
        originDoc.Transact(transaction =>
        {
            int size = sv.Values.Count(clock => clock > 0);
            encoder.RestEncoder.WriteVarUint(size);
            // splitting the structs before writing them to the encoder
            foreach (var (client, clock) in sv)
            {
                if (clock == 0) continue;
                if (clock < originDoc.Store.GetState(client)) originDoc.Store.GetItemCleanStart(transaction, new ID(client, clock));
                var structs = originDoc.Store.Clients.TryGetValue(client, out var s) ? s : new List<AbstractStruct>();
                int lastStructIndex = StructStore.FindIndexSS(structs, clock - 1);
                // write # encoded structs
                encoder.RestEncoder.WriteVarUint(lastStructIndex + 1);
                encoder.WriteClient(client);
                // first clock written is 0
                encoder.RestEncoder.WriteVarUint(0);
                for (int i = 0; i <= lastStructIndex; i++) structs[i].Write(encoder, 0);
            }
            DeleteSet.Write(encoder, ds);
        });
        UpdateCodec.ApplyUpdateV2(newDoc, encoder.ToArray(), "snapshot");
        return newDoc;
    }

    public bool ContainsUpdateV2(byte[] update, Func<Decoder, IUpdateDecoder>? decoderFactory = null)
    {
        var updateDecoder = (decoderFactory ?? (d => new UpdateDecoderV2(d)))(new Decoder(update));
        var lazyDecoder = new LazyStructReader(updateDecoder, false);
        for (var curr = lazyDecoder.Curr; curr != null; curr = lazyDecoder.Next())
        {
            if (Sv.GetValueOrDefault(curr.Id.Client) < curr.Id.Clock + curr.Length) return false;
        }
        var mergedDS = DeleteSet.Merge(new[] { Ds, DeleteSet.Read(updateDecoder) });
        return DeleteSet.Equal(Ds, mergedDS);
    }

    public bool ContainsUpdate(byte[] update) => ContainsUpdateV2(update, d => new UpdateDecoderV1(d));
}
