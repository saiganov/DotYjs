namespace DotYjs;

internal sealed class PendingStructs
{
    public PendingStructs(Dictionary<long, long> missing, byte[] update)
    {
        Missing = missing;
        Update = update;
    }

    public Dictionary<long, long> Missing { get; }
    public byte[] Update { get; set; }
}

/// <summary>Stores all structs of a document, grouped per client and sorted by clock.</summary>
public sealed class StructStore
{
    /// <summary>The structs of every client, sorted by clock.</summary>
    public Dictionary<long, List<AbstractStruct>> Clients { get; } = new();

    internal PendingStructs? PendingStructs { get; set; }
    internal byte[]? PendingDs { get; set; }

    /// <summary>Returns the state vector: for each client the next expected clock.</summary>
    public Dictionary<long, long> GetStateVector()
    {
        var sm = new Dictionary<long, long>();
        foreach (var (client, structs) in Clients)
        {
            var s = structs[^1];
            sm[client] = s.Id.Clock + s.Length;
        }
        return sm;
    }

    /// <summary>The next clock of <paramref name="client"/>, which is the number of elements it has created (0 for an unknown client).</summary>
    public long GetState(long client)
    {
        if (!Clients.TryGetValue(client, out var structs)) return 0;
        var last = structs[^1];
        return last.Id.Clock + last.Length;
    }

    internal void IntegrityCheck()
    {
        foreach (var structs in Clients.Values)
        {
            for (int i = 1; i < structs.Count; i++)
            {
                var l = structs[i - 1];
                var r = structs[i];
                if (l.Id.Clock + l.Length != r.Id.Clock) throw new InvalidOperationException("StructStore failed integrity check");
            }
        }
    }

    internal void AddStruct(AbstractStruct s)
    {
        if (!Clients.TryGetValue(s.Id.Client, out var structs))
        {
            structs = new List<AbstractStruct>();
            Clients[s.Id.Client] = structs;
        }
        else
        {
            var last = structs[^1];
            if (last.Id.Clock + last.Length != s.Id.Clock) throw new InvalidOperationException("Unexpected case");
        }
        structs.Add(s);
    }

    /// <summary>Binary search for the index of the struct containing <paramref name="clock"/>.</summary>
    internal static int FindIndexSS(List<AbstractStruct> structs, long clock)
    {
        int left = 0;
        int right = structs.Count - 1;
        var mid = structs[right];
        long midclock = mid.Id.Clock;
        if (midclock == clock) return right;
        // pivoting the search
        int midindex = (int)Math.Floor((double)clock / (midclock + mid.Length - 1) * right);
        while (left <= right)
        {
            if (midindex < left || midindex > right) midindex = (left + right) / 2;
            mid = structs[midindex];
            midclock = mid.Id.Clock;
            if (midclock <= clock)
            {
                if (clock < midclock + mid.Length) return midindex;
                left = midindex + 1;
            }
            else
            {
                right = midindex - 1;
            }
            midindex = (left + right) / 2;
        }
        throw new InvalidOperationException("Unexpected case");
    }

    internal AbstractStruct Find(ID id)
    {
        var structs = Clients[id.Client];
        return structs[FindIndexSS(structs, id.Clock)];
    }

    internal Item GetItem(ID id) => (Item)Find(id);

    internal static int FindIndexCleanStart(Transaction transaction, List<AbstractStruct> structs, long clock)
    {
        int index = FindIndexSS(structs, clock);
        var s = structs[index];
        if (s.Id.Clock < clock && s is Item item)
        {
            structs.Insert(index + 1, Item.SplitItem(transaction, item, clock - item.Id.Clock));
            return index + 1;
        }
        return index;
    }

    /// <summary>Returns the struct starting exactly at <paramref name="id"/>, splitting if necessary.</summary>
    internal AbstractStruct GetItemCleanStart(Transaction transaction, ID id)
    {
        var structs = Clients[id.Client];
        return structs[FindIndexCleanStart(transaction, structs, id.Clock)];
    }

    /// <summary>Returns the struct ending exactly at <paramref name="id"/>, splitting if necessary.</summary>
    internal AbstractStruct GetItemCleanEnd(Transaction transaction, ID id)
    {
        var structs = Clients[id.Client];
        int index = FindIndexSS(structs, id.Clock);
        var s = structs[index];
        if (id.Clock != s.Id.Clock + s.Length - 1 && s is Item item)
        {
            structs.Insert(index + 1, Item.SplitItem(transaction, item, id.Clock - item.Id.Clock + 1));
        }
        return s;
    }

    internal void ReplaceStruct(AbstractStruct s, AbstractStruct newStruct)
    {
        var structs = Clients[s.Id.Client];
        structs[FindIndexSS(structs, s.Id.Clock)] = newStruct;
    }

    internal static void IterateStructs(Transaction transaction, List<AbstractStruct> structs, long clockStart, long len, Action<AbstractStruct> f)
    {
        if (len == 0) return;
        long clockEnd = clockStart + len;
        int index = FindIndexCleanStart(transaction, structs, clockStart);
        AbstractStruct s;
        do
        {
            s = structs[index++];
            if (clockEnd < s.Id.Clock + s.Length) FindIndexCleanStart(transaction, structs, clockEnd);
            f(s);
        } while (index < structs.Count && structs[index].Id.Clock < clockEnd);
    }
}
