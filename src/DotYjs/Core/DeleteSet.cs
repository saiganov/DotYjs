namespace DotYjs;

/// <summary>A range of deleted elements of one client: <see cref="Len"/> elements starting at <see cref="Clock"/>.</summary>
public sealed class DeleteItem
{
    /// <summary>Creates a deleted range.</summary>
    public DeleteItem(long clock, long len)
    {
        Clock = clock;
        Len = len;
    }

    /// <summary>The clock of the first deleted element.</summary>
    public long Clock { get; }
    /// <summary>The number of deleted elements.</summary>
    public long Len { get; internal set; }

    /// <inheritdoc />
    public override string ToString() => $"[{Clock},{Len}]";
}

/// <summary>
/// A set of deleted ranges per client. Unlike the struct store, a delete set is compact: it does not store the
/// deleted content, only the ranges.
/// </summary>
public sealed class DeleteSet
{
    /// <summary>Deleted ranges by client id, sorted by clock after the delete set is sorted and merged.</summary>
    public Dictionary<long, List<DeleteItem>> Clients { get; } = new();

    internal void Add(long client, long clock, long length)
    {
        if (!Clients.TryGetValue(client, out var list))
        {
            list = new List<DeleteItem>();
            Clients[client] = list;
        }
        list.Add(new DeleteItem(clock, length));
    }

    internal static void IterateDeletedStructs(Transaction transaction, DeleteSet ds, Action<AbstractStruct> f)
    {
        foreach (var (clientid, deletes) in ds.Clients)
        {
            if (transaction.Doc.Store.Clients.TryGetValue(clientid, out var structs))
            {
                var lastStruct = structs[^1];
                long clockState = lastStruct.Id.Clock + lastStruct.Length;
                for (int i = 0; i < deletes.Count && deletes[i].Clock < clockState; i++)
                {
                    var del = deletes[i];
                    StructStore.IterateStructs(transaction, structs, del.Clock, del.Len, f);
                }
            }
        }
    }

    internal static int? FindIndexDS(List<DeleteItem> dis, long clock)
    {
        int left = 0;
        int right = dis.Count - 1;
        while (left <= right)
        {
            int midindex = (left + right) / 2;
            var mid = dis[midindex];
            long midclock = mid.Clock;
            if (midclock <= clock)
            {
                if (clock < midclock + mid.Len) return midindex;
                left = midindex + 1;
            }
            else
            {
                right = midindex - 1;
            }
        }
        return null;
    }

    /// <summary>Whether the element with the given id is in this delete set.</summary>
    public bool IsDeleted(ID id) => Clients.TryGetValue(id.Client, out var dis) && FindIndexDS(dis, id.Clock) != null;

    internal void SortAndMerge()
    {
        foreach (var dels in Clients.Values)
        {
            dels.Sort((a, b) => a.Clock.CompareTo(b.Clock));
            int i, j;
            for (i = 1, j = 1; i < dels.Count; i++)
            {
                var left = dels[j - 1];
                var right = dels[i];
                if (left.Clock + left.Len >= right.Clock)
                {
                    left.Len = Math.Max(left.Len, right.Clock + right.Len - left.Clock);
                }
                else
                {
                    if (j < i) dels[j] = right;
                    j++;
                }
            }
            if (j < dels.Count) dels.RemoveRange(j, dels.Count - j);
        }
    }

    internal static DeleteSet Merge(IReadOnlyList<DeleteSet> dss)
    {
        var merged = new DeleteSet();
        for (int dssI = 0; dssI < dss.Count; dssI++)
        {
            foreach (var (client, delsLeft) in dss[dssI].Clients)
            {
                if (!merged.Clients.ContainsKey(client))
                {
                    var dels = delsLeft.Select(d => new DeleteItem(d.Clock, d.Len)).ToList();
                    for (int i = dssI + 1; i < dss.Count; i++)
                    {
                        if (dss[i].Clients.TryGetValue(client, out var other)) dels.AddRange(other.Select(d => new DeleteItem(d.Clock, d.Len)));
                    }
                    merged.Clients[client] = dels;
                }
            }
        }
        merged.SortAndMerge();
        return merged;
    }

    internal static DeleteSet FromStructStore(StructStore ss)
    {
        var ds = new DeleteSet();
        foreach (var (client, structs) in ss.Clients)
        {
            var dsitems = new List<DeleteItem>();
            for (int i = 0; i < structs.Count; i++)
            {
                var s = structs[i];
                if (s.Deleted)
                {
                    long clock = s.Id.Clock;
                    long len = s.Length;
                    while (i + 1 < structs.Count && structs[i + 1].Deleted)
                    {
                        len += structs[i + 1].Length;
                        i++;
                    }
                    dsitems.Add(new DeleteItem(clock, len));
                }
            }
            if (dsitems.Count > 0) ds.Clients[client] = dsitems;
        }
        return ds;
    }

    internal static void Write(IDSEncoder encoder, DeleteSet ds)
    {
        encoder.RestEncoder.WriteVarUint(ds.Clients.Count);
        foreach (var (client, dsitems) in ds.Clients.OrderByDescending(kv => kv.Key))
        {
            encoder.ResetDsCurVal();
            encoder.RestEncoder.WriteVarUint(client);
            encoder.RestEncoder.WriteVarUint(dsitems.Count);
            foreach (var item in dsitems)
            {
                encoder.WriteDsClock(item.Clock);
                encoder.WriteDsLen(item.Len);
            }
        }
    }

    internal static DeleteSet Read(IDSDecoder decoder)
    {
        var ds = new DeleteSet();
        long numClients = decoder.RestDecoder.ReadVarUint();
        for (long i = 0; i < numClients; i++)
        {
            decoder.ResetDsCurVal();
            long client = decoder.RestDecoder.ReadVarUint();
            long numberOfDeletes = decoder.RestDecoder.ReadVarUint();
            if (numberOfDeletes > 0)
            {
                if (!ds.Clients.TryGetValue(client, out var dsField))
                {
                    dsField = new List<DeleteItem>();
                    ds.Clients[client] = dsField;
                }
                for (long j = 0; j < numberOfDeletes; j++)
                {
                    long clock = decoder.ReadDsClock();
                    long len = UpdateCodec.CheckLength(decoder.ReadDsLen());
                    // encoded delete sets are sorted and merged; anything else cannot be re-encoded (v2 stores clock differences)
                    if (dsField.Count > 0 && clock < dsField[^1].Clock + dsField[^1].Len) throw new InvalidDataException("Delete set is not sorted");
                    UpdateCodec.CheckClock(clock + len);
                    dsField.Add(new DeleteItem(clock, len));
                }
            }
        }
        return ds;
    }

    /// <summary>Reads a delete set and applies it. Returns a v2 update with the unapplied deletes, or null.</summary>
    internal static byte[]? ReadAndApply(IDSDecoder decoder, Transaction transaction, StructStore store)
    {
        var unappliedDS = new DeleteSet();
        var lastEnd = new Dictionary<long, long>();
        long numClients = decoder.RestDecoder.ReadVarUint();
        for (long i = 0; i < numClients; i++)
        {
            decoder.ResetDsCurVal();
            long client = decoder.RestDecoder.ReadVarUint();
            long numberOfDeletes = decoder.RestDecoder.ReadVarUint();
            var structs = store.Clients.TryGetValue(client, out var s0) ? s0 : new List<AbstractStruct>();
            long state = store.GetState(client);
            for (long j = 0; j < numberOfDeletes; j++)
            {
                long clock = decoder.ReadDsClock();
                long clockEnd = UpdateCodec.CheckClock(clock + UpdateCodec.CheckLength(decoder.ReadDsLen()));
                // encoded delete sets are sorted and merged (see Read)
                if (clock < lastEnd.GetValueOrDefault(client)) throw new InvalidDataException("Delete set is not sorted");
                lastEnd[client] = clockEnd;
                if (clock < state)
                {
                    if (state < clockEnd) unappliedDS.Add(client, state, clockEnd - state);
                    int index = StructStore.FindIndexSS(structs, clock);
                    // We can ignore the case of GC and Delete structs, because we are going to skip them
                    var s = structs[index];
                    // split the first item if necessary
                    if (!s.Deleted && s.Id.Clock < clock)
                    {
                        structs.Insert(index + 1, Item.SplitItem(transaction, (Item)s, clock - s.Id.Clock));
                        index++; // increase we now want to use the next struct
                    }
                    while (index < structs.Count)
                    {
                        s = structs[index++];
                        if (s.Id.Clock < clockEnd)
                        {
                            if (!s.Deleted)
                            {
                                if (clockEnd < s.Id.Clock + s.Length)
                                {
                                    structs.Insert(index, Item.SplitItem(transaction, (Item)s, clockEnd - s.Id.Clock));
                                }
                                ((Item)s).Delete(transaction);
                            }
                        }
                        else
                        {
                            break;
                        }
                    }
                }
                else
                {
                    unappliedDS.Add(client, clock, clockEnd - clock);
                }
            }
        }
        if (unappliedDS.Clients.Count > 0)
        {
            var ds = new UpdateEncoderV2();
            ds.RestEncoder.WriteVarUint(0); // encode 0 structs
            Write(ds, unappliedDS);
            return ds.ToArray();
        }
        return null;
    }

    internal static bool Equal(DeleteSet ds1, DeleteSet ds2)
    {
        if (ds1.Clients.Count != ds2.Clients.Count) return false;
        foreach (var (client, items1) in ds1.Clients)
        {
            if (!ds2.Clients.TryGetValue(client, out var items2) || items1.Count != items2.Count) return false;
            for (int i = 0; i < items1.Count; i++)
            {
                if (items1[i].Clock != items2[i].Clock || items1[i].Len != items2[i].Len) return false;
            }
        }
        return true;
    }
}
