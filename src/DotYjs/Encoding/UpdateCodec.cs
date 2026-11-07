using DotYjs.Lib0;

namespace DotYjs;

internal sealed class StructRefs
{
    public int I;
    public List<AbstractStruct> Refs;
    public StructRefs(List<AbstractStruct> refs) { Refs = refs; }
}

/// <summary>
/// Encoding / decoding of document updates and state vectors (yjs <c>utils/encoding.js</c>).
/// </summary>
public static class UpdateCodec
{
    private static void WriteStructs(IUpdateEncoder encoder, List<AbstractStruct> structs, long client, long clock)
    {
        // write first id
        clock = Math.Max(clock, structs[0].Id.Clock); // make sure the first id exists
        int startNewStructs = StructStore.FindIndexSS(structs, clock);
        // write # encoded structs
        encoder.RestEncoder.WriteVarUint(structs.Count - startNewStructs);
        encoder.WriteClient(client);
        encoder.RestEncoder.WriteVarUint(clock);
        var firstStruct = structs[startNewStructs];
        // write first struct with an offset
        firstStruct.Write(encoder, clock - firstStruct.Id.Clock);
        for (int i = startNewStructs + 1; i < structs.Count; i++) structs[i].Write(encoder, 0);
    }

    internal static void WriteClientsStructs(IUpdateEncoder encoder, StructStore store, Dictionary<long, long> _sm)
    {
        // we filter all valid _sm entries into sm
        var sm = new Dictionary<long, long>();
        foreach (var (client, clock) in _sm)
        {
            // only write if new structs are available
            if (store.GetState(client) > clock) sm[client] = clock;
        }
        foreach (var (client, _) in store.GetStateVector())
        {
            if (!_sm.ContainsKey(client)) sm[client] = 0;
        }
        // write # states that were updated
        encoder.RestEncoder.WriteVarUint(sm.Count);
        // Write items with higher client ids first
        // This heavily improves the conflict algorithm.
        foreach (var (client, clock) in sm.OrderByDescending(kv => kv.Key))
        {
            WriteStructs(encoder, store.Clients[client], client, clock);
        }
    }

    internal static Dictionary<long, StructRefs> ReadClientsStructRefs(IUpdateDecoder decoder, Doc doc)
    {
        var clientRefs = new Dictionary<long, StructRefs>();
        long numOfStateUpdates = decoder.RestDecoder.ReadVarUint();
        for (long i = 0; i < numOfStateUpdates; i++)
        {
            long numberOfStructs = decoder.RestDecoder.ReadVarUint();
            var refs = new List<AbstractStruct>((int)Math.Min(numberOfStructs, 1024)); // the count may be corrupt; the list grows as needed
            long client = CheckClient(decoder.ReadClient());
            long clock = decoder.RestDecoder.ReadVarUint();
            clientRefs[client] = new StructRefs(refs);
            for (long j = 0; j < numberOfStructs; j++)
            {
                byte info = decoder.ReadInfo();
                switch (info & 0x1f)
                {
                    case 0:
                        {
                            long len = CheckLength(decoder.ReadLen());
                            refs.Add(new GC(new ID(client, clock), len));
                            clock = CheckClock(clock + len);
                            break;
                        }
                    case 10:
                        {
                            long len = CheckLength(decoder.RestDecoder.ReadVarUint());
                            refs.Add(new Skip(new ID(client, clock), len));
                            clock = CheckClock(clock + len);
                            break;
                        }
                    default:
                        {
                            var s = ReadItem(decoder, client, clock, info, doc);
                            refs.Add(s);
                            clock = CheckClock(clock + s.Length);
                            break;
                        }
                }
            }
        }
        return clientRefs;
    }

    // Checks of decoded values that valid updates always satisfy. They reject corrupt or malicious input while the
    // update is decoded, before anything is applied to the document.

    internal static long CheckClient(long client) =>
        client >= 0 ? client : throw new InvalidDataException("Invalid client id");

    internal static long CheckLength(long len) =>
        len > 0 ? len : throw new InvalidDataException("Invalid struct length");

    /// <summary>Clocks are JavaScript numbers, so yjs (and the lib0 var uint decoder) cannot go beyond 2^53 - 1.</summary>
    internal static long CheckClock(long clock) =>
        clock is >= 0 and <= MaxClock ? clock : throw new InvalidDataException("Clock out of range");

    private const long MaxClock = (1L << 53) - 1;

    /// <summary>An origin or parent of an item must exist before the item: from the same client it has a lower clock.</summary>
    private static ID CheckReference(ID id, long client, long clock) =>
        id.Client >= 0 && id.Clock >= 0 && (id.Client != client || id.Clock < clock)
            ? id
            : throw new InvalidDataException("Invalid reference to another struct");

    internal static Item ReadItem(IUpdateDecoder decoder, long client, long clock, byte info, Doc? doc)
    {
        bool cantCopyParentInfo = (info & (0x40 | 0x80)) == 0;
        var origin = (info & 0x80) == 0x80 ? CheckReference(decoder.ReadLeftID(), client, clock) : null;
        var rightOrigin = (info & 0x40) == 0x40 ? CheckReference(decoder.ReadRightID(), client, clock) : null;
        object? parent = null;
        if (cantCopyParentInfo)
        {
            if (decoder.ReadParentInfo())
            {
                var name = decoder.ReadString();
                parent = doc != null ? doc.Get(name) : name;
            }
            else
            {
                parent = CheckReference(decoder.ReadLeftID(), client, clock);
            }
        }
        string? parentSub = cantCopyParentInfo && (info & 0x20) == 0x20 ? decoder.ReadString() : null;
        var content = AbstractContent.Read(decoder, info);
        return new Item(new ID(client, clock), null, origin, null, rightOrigin, parent, parentSub, content);
    }

    /// <summary>
    /// Resume computing structs generated by struct readers.
    /// While there is something to do, we integrate structs in this order:
    /// 1. top element on stack, if stack is not empty
    /// 2. next element from current struct reader (if empty, use next struct reader)
    /// If struct causally depends on another struct (ref.missing), we put next reader of
    /// `ref.id.client` on top of stack.
    /// At some point we find a struct that has no causal dependencies, then we start emptying the stack.
    /// It is not possible to have cycles: i.e. struct1 (from client1) depends on struct2 (from client2)
    /// depends on struct3 (from client1). Therefore the max stack size is equal to `structReaders.length`.
    /// This method is implemented in a way so that we can resume computation if this update
    /// causally depends on another update.
    /// </summary>
    private static PendingStructs? IntegrateStructs(Transaction transaction, StructStore store, Dictionary<long, StructRefs> clientsStructRefs)
    {
        var stack = new List<AbstractStruct>();
        // sort them so that we take the higher id first, in case of conflicts the lower id will probably not conflict with the id from the higher user.
        var clientsStructRefsIds = clientsStructRefs.Keys.OrderBy(k => k).ToList();
        if (clientsStructRefsIds.Count == 0) return null;

        StructRefs? GetNextStructTarget()
        {
            if (clientsStructRefsIds.Count == 0) return null;
            var nextStructsTarget = clientsStructRefs[clientsStructRefsIds[^1]];
            while (nextStructsTarget.Refs.Count == nextStructsTarget.I)
            {
                clientsStructRefsIds.RemoveAt(clientsStructRefsIds.Count - 1);
                if (clientsStructRefsIds.Count > 0) nextStructsTarget = clientsStructRefs[clientsStructRefsIds[^1]];
                else return null;
            }
            return nextStructsTarget;
        }

        var curStructsTarget = GetNextStructTarget();
        if (curStructsTarget == null) return null;

        var restStructs = new StructStore();
        var missingSV = new Dictionary<long, long>();
        void UpdateMissingSv(long client, long clock)
        {
            if (!missingSV.TryGetValue(client, out var mclock) || mclock > clock) missingSV[client] = clock;
        }
        var stackHead = curStructsTarget.Refs[curStructsTarget.I++];
        // caching the state because it is used very often
        var state = new Dictionary<long, long>();

        void AddStackToRestSS()
        {
            foreach (var item in stack)
            {
                long client = item.Id.Client;
                if (clientsStructRefs.TryGetValue(client, out var inapplicableItems))
                {
                    // decrement because we weren't able to apply previous operation
                    inapplicableItems.I--;
                    restStructs.Clients[client] = inapplicableItems.Refs.GetRange(inapplicableItems.I, inapplicableItems.Refs.Count - inapplicableItems.I);
                    clientsStructRefs.Remove(client);
                    inapplicableItems.I = 0;
                    inapplicableItems.Refs = new List<AbstractStruct>();
                }
                else
                {
                    // item was the last item on clientsStructRefs and the field was already cleared. Add item to restStructs and continue
                    restStructs.Clients[client] = new List<AbstractStruct> { item };
                }
                // remove client from clientsStructRefsIds to prevent users from applying the same update again
                clientsStructRefsIds.RemoveAll(c => c == client);
            }
            stack.Clear();
        }

        // iterate over all struct readers until we are done
        while (true)
        {
            if (stackHead is not Skip)
            {
                if (!state.TryGetValue(stackHead.Id.Client, out var localClock))
                {
                    localClock = store.GetState(stackHead.Id.Client);
                    state[stackHead.Id.Client] = localClock;
                }
                long offset = localClock - stackHead.Id.Clock;
                if (offset < 0)
                {
                    // update from the same client is missing
                    stack.Add(stackHead);
                    UpdateMissingSv(stackHead.Id.Client, stackHead.Id.Clock - 1);
                    // hid a dead wall, add all items from stack to restSS
                    AddStackToRestSS();
                }
                else
                {
                    var missing = stackHead.GetMissing(transaction, store);
                    if (missing != null)
                    {
                        stack.Add(stackHead);
                        // get the struct reader that has the missing struct
                        var structRefs = clientsStructRefs.TryGetValue(missing.Value, out var sr) ? sr : new StructRefs(new List<AbstractStruct>());
                        if (structRefs.Refs.Count == structRefs.I)
                        {
                            // This update message causally depends on another update message that doesn't exist yet
                            UpdateMissingSv(missing.Value, store.GetState(missing.Value));
                            AddStackToRestSS();
                        }
                        else
                        {
                            stackHead = structRefs.Refs[structRefs.I++];
                            continue;
                        }
                    }
                    else if (offset == 0 || offset < stackHead.Length)
                    {
                        // all fine, apply the stackhead
                        stackHead.Integrate(transaction, offset);
                        state[stackHead.Id.Client] = stackHead.Id.Clock + stackHead.Length;
                    }
                }
            }
            // iterate to next stackHead
            if (stack.Count > 0)
            {
                stackHead = stack[^1];
                stack.RemoveAt(stack.Count - 1);
            }
            else if (curStructsTarget != null && curStructsTarget.I < curStructsTarget.Refs.Count)
            {
                stackHead = curStructsTarget.Refs[curStructsTarget.I++];
            }
            else
            {
                curStructsTarget = GetNextStructTarget();
                if (curStructsTarget == null)
                {
                    // we are done!
                    break;
                }
                stackHead = curStructsTarget.Refs[curStructsTarget.I++];
            }
        }
        if (restStructs.Clients.Count > 0)
        {
            var encoder = new UpdateEncoderV2();
            WriteClientsStructs(encoder, restStructs, new Dictionary<long, long>());
            // write empty deleteset
            // writeDeleteSet(encoder, new DeleteSet())
            encoder.RestEncoder.WriteVarUint(0); // => no need for an extra function call, just write 0 deletes
            return new PendingStructs(missingSV, encoder.ToArray());
        }
        return null;
    }

    internal static void WriteStructsFromTransaction(IUpdateEncoder encoder, Transaction transaction)
        => WriteClientsStructs(encoder, transaction.Doc.Store, transaction.BeforeState);

    /// <summary>Read and apply a document update.</summary>
    public static void ReadUpdateV2(Decoder decoder, Doc ydoc, object? transactionOrigin = null, IUpdateDecoder? structDecoder = null)
    {
        structDecoder ??= new UpdateDecoderV2(decoder);
        Transaction.Transact(ydoc, transaction =>
        {
            // force that transaction.local is set to non-local
            transaction.Local = false;
            bool retry = false;
            var doc = transaction.Doc;
            var store = doc.Store;
            var ss = ReadClientsStructRefs(structDecoder, doc);
            var restStructs = IntegrateStructs(transaction, store, ss);
            var pending = store.PendingStructs;
            if (pending != null)
            {
                // check if we can apply something
                foreach (var (client, clock) in pending.Missing)
                {
                    if (clock < store.GetState(client))
                    {
                        retry = true;
                        break;
                    }
                }
                if (restStructs != null)
                {
                    // merge restStructs into store.pending
                    foreach (var (client, clock) in restStructs.Missing)
                    {
                        if (!pending.Missing.TryGetValue(client, out var mclock) || mclock > clock) pending.Missing[client] = clock;
                    }
                    pending.Update = Updates.MergeUpdatesV2(new[] { pending.Update, restStructs.Update });
                }
            }
            else
            {
                store.PendingStructs = restStructs;
            }
            var dsRest = DeleteSet.ReadAndApply(structDecoder, transaction, store);
            if (store.PendingDs != null)
            {
                // @todo we could make a lower-bound state-vector check as we do above
                var pendingDSUpdate = new UpdateDecoderV2(new Decoder(store.PendingDs));
                pendingDSUpdate.RestDecoder.ReadVarUint(); // read 0 structs, because we only encode deletes in pendingdsupdate
                var dsRest2 = DeleteSet.ReadAndApply(pendingDSUpdate, transaction, store);
                if (dsRest != null && dsRest2 != null)
                {
                    // case 1: ds1 != null && ds2 != null
                    store.PendingDs = Updates.MergeUpdatesV2(new[] { dsRest, dsRest2 });
                }
                else
                {
                    // case 2: ds1 != null
                    // case 3: ds2 != null
                    // case 4: ds1 == null && ds2 == null
                    store.PendingDs = dsRest ?? dsRest2;
                }
            }
            else
            {
                // Either dsRest == null && pendingDs == null OR dsRest != null
                store.PendingDs = dsRest;
            }
            if (retry)
            {
                var update = store.PendingStructs!.Update;
                store.PendingStructs = null;
                ApplyUpdateV2(transaction.Doc, update);
            }
        }, transactionOrigin, false);
    }

    public static void ReadUpdate(Decoder decoder, Doc ydoc, object? transactionOrigin = null)
        => ReadUpdateV2(decoder, ydoc, transactionOrigin, new UpdateDecoderV1(decoder));

    /// <summary>Apply a v2-encoded document update created by e.g. <see cref="EncodeStateAsUpdateV2"/>.</summary>
    public static void ApplyUpdateV2(Doc ydoc, byte[] update, object? transactionOrigin = null, Func<Decoder, IUpdateDecoder>? decoderFactory = null)
    {
        var decoder = new Decoder(update);
        ReadUpdateV2(decoder, ydoc, transactionOrigin, (decoderFactory ?? (d => new UpdateDecoderV2(d)))(decoder));
    }

    /// <summary>Apply a v1-encoded document update (the default format of yjs).</summary>
    public static void ApplyUpdate(Doc ydoc, byte[] update, object? transactionOrigin = null)
        => ApplyUpdateV2(ydoc, update, transactionOrigin, d => new UpdateDecoderV1(d));

    /// <summary>Write all the document as a single update message that can be applied on the remote document.</summary>
    internal static void WriteStateAsUpdate(IUpdateEncoder encoder, Doc doc, Dictionary<long, long>? targetStateVector = null)
    {
        WriteClientsStructs(encoder, doc.Store, targetStateVector ?? new Dictionary<long, long>());
        DeleteSet.Write(encoder, DeleteSet.FromStructStore(doc.Store));
    }

    /// <summary>
    /// Write all the document as a single update message. If you specify the state of the remote client
    /// (<paramref name="encodedTargetStateVector"/>) it will only write the operations that are missing.
    /// </summary>
    public static byte[] EncodeStateAsUpdateV2(Doc doc, byte[]? encodedTargetStateVector = null, IUpdateEncoder? encoder = null)
    {
        encoder ??= new UpdateEncoderV2();
        encodedTargetStateVector ??= new byte[] { 0 };
        var targetStateVector = DecodeStateVector(encodedTargetStateVector);
        WriteStateAsUpdate(encoder, doc, targetStateVector);
        var updates = new List<byte[]> { encoder.ToArray() };
        // also add the pending updates (if there are any)
        if (doc.Store.PendingDs != null) updates.Add(doc.Store.PendingDs);
        if (doc.Store.PendingStructs != null) updates.Add(Updates.DiffUpdateV2(doc.Store.PendingStructs.Update, encodedTargetStateVector));
        if (updates.Count > 1)
        {
            if (encoder is UpdateEncoderV1)
            {
                return Updates.MergeUpdates(updates.Select((update, i) => i == 0 ? update : Updates.ConvertUpdateFormatV2ToV1(update)).ToList());
            }
            return Updates.MergeUpdatesV2(updates);
        }
        return updates[0];
    }

    public static byte[] EncodeStateAsUpdate(Doc doc, byte[]? encodedTargetStateVector = null)
        => EncodeStateAsUpdateV2(doc, encodedTargetStateVector, new UpdateEncoderV1());

    /// <summary>Read state vector from Decoder and return as Map.</summary>
    public static Dictionary<long, long> ReadStateVector(IDSDecoder decoder)
    {
        var ss = new Dictionary<long, long>();
        long ssLength = decoder.RestDecoder.ReadVarUint();
        for (long i = 0; i < ssLength; i++)
        {
            long client = decoder.RestDecoder.ReadVarUint();
            long clock = decoder.RestDecoder.ReadVarUint();
            ss[client] = clock;
        }
        return ss;
    }

    /// <summary>Read decodedState and return State as Map.</summary>
    public static Dictionary<long, long> DecodeStateVector(byte[] decodedState) => ReadStateVector(new DSDecoderV1(new Decoder(decodedState)));

    internal static IDSEncoder WriteStateVector(IDSEncoder encoder, Dictionary<long, long> sv)
    {
        encoder.RestEncoder.WriteVarUint(sv.Count);
        foreach (var (client, clock) in sv.OrderByDescending(kv => kv.Key))
        {
            encoder.RestEncoder.WriteVarUint(client);
            encoder.RestEncoder.WriteVarUint(clock);
        }
        return encoder;
    }

    internal static void WriteDocumentStateVector(IDSEncoder encoder, Doc doc) => WriteStateVector(encoder, doc.Store.GetStateVector());

    /// <summary>Encode State as Uint8Array.</summary>
    public static byte[] EncodeStateVectorV2(Doc doc, IDSEncoder? encoder = null)
    {
        encoder ??= new DSEncoderV2();
        WriteDocumentStateVector(encoder, doc);
        return encoder.ToArray();
    }

    public static byte[] EncodeStateVectorV2(Dictionary<long, long> sv, IDSEncoder? encoder = null)
    {
        encoder ??= new DSEncoderV2();
        WriteStateVector(encoder, sv);
        return encoder.ToArray();
    }

    public static byte[] EncodeStateVector(Doc doc) => EncodeStateVectorV2(doc, new DSEncoderV1());

    public static byte[] EncodeStateVector(Dictionary<long, long> sv) => EncodeStateVectorV2(sv, new DSEncoderV1());
}
