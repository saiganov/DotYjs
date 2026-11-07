using DotYjs.Lib0;

namespace DotYjs;

/// <summary>Reads structs lazily from an update, without integrating them into a document.</summary>
internal sealed class LazyStructReader : IDisposable
{
    private readonly IEnumerator<AbstractStruct> _gen;
    private readonly bool _filterSkips;

    public AbstractStruct? Curr { get; private set; }

    public void Dispose() => _gen.Dispose();

    public LazyStructReader(IUpdateDecoder decoder, bool filterSkips)
    {
        _gen = Generate(decoder).GetEnumerator();
        _filterSkips = filterSkips;
        Next();
    }

    public AbstractStruct? Next()
    {
        // ignore "Skip" structs
        do
        {
            Curr = _gen.MoveNext() ? _gen.Current : null;
        } while (_filterSkips && Curr is Skip);
        return Curr;
    }

    private static IEnumerable<AbstractStruct> Generate(IUpdateDecoder decoder)
    {
        long numOfStateUpdates = decoder.RestDecoder.ReadVarUint();
        for (long i = 0; i < numOfStateUpdates; i++)
        {
            long numberOfStructs = decoder.RestDecoder.ReadVarUint();
            // decoder.readClient() must be called before
            long client = decoder.ReadClient();
            long clock = decoder.RestDecoder.ReadVarUint();
            for (long j = 0; j < numberOfStructs; j++)
            {
                byte info = decoder.ReadInfo();
                // @todo use switch instead of ifs
                if (info == 10)
                {
                    long len = decoder.RestDecoder.ReadVarUint();
                    yield return new Skip(new ID(client, clock), len);
                    clock += len;
                }
                else if ((info & 0x1f) != 0)
                {
                    var s = UpdateCodec.ReadItem(decoder, client, clock, info, null);
                    yield return s;
                    clock += s.Length;
                }
                else
                {
                    long len = decoder.ReadLen();
                    yield return new GC(new ID(client, clock), len);
                    clock += len;
                }
            }
        }
    }
}

internal sealed class LazyStructWriter
{
    public long CurrClient;
    public long StartClock;
    public int Written;
    public IUpdateEncoder Encoder;
    public List<(int written, byte[] restEncoder)> ClientStructs = new();

    public LazyStructWriter(IUpdateEncoder encoder) { Encoder = encoder; }
}

/// <summary>A decoded update: the list of structs and the delete set.</summary>
public sealed class DecodedUpdate
{
    public DecodedUpdate(List<AbstractStruct> structs, DeleteSet ds)
    {
        Structs = structs;
        Ds = ds;
    }

    public List<AbstractStruct> Structs { get; }
    public DeleteSet Ds { get; }
}

/// <summary>Functions that operate on encoded updates without a <see cref="Doc"/> (yjs <c>utils/updates.js</c>).</summary>
public static class Updates
{
    private static Func<Decoder, IUpdateDecoder> V1Decoder => d => new UpdateDecoderV1(d);
    private static Func<Decoder, IUpdateDecoder> V2Decoder => d => new UpdateDecoderV2(d);
    private static Func<IUpdateEncoder> V1Encoder => () => new UpdateEncoderV1();
    private static Func<IUpdateEncoder> V2Encoder => () => new UpdateEncoderV2();

    public static DecodedUpdate DecodeUpdate(byte[] update) => DecodeUpdateV2(update, V1Decoder);

    public static DecodedUpdate DecodeUpdateV2(byte[] update, Func<Decoder, IUpdateDecoder>? decoderFactory = null)
    {
        var structs = new List<AbstractStruct>();
        var updateDecoder = (decoderFactory ?? V2Decoder)(new Decoder(update));
        var lazyDecoder = new LazyStructReader(updateDecoder, false);
        for (var curr = lazyDecoder.Curr; curr != null; curr = lazyDecoder.Next()) structs.Add(curr);
        return new DecodedUpdate(structs, DeleteSet.Read(updateDecoder));
    }

    public static byte[] MergeUpdates(IReadOnlyList<byte[]> updates) => MergeUpdatesV2(updates, V1Decoder, V1Encoder);

    public static byte[] EncodeStateVectorFromUpdateV2(byte[] update, Func<IDSEncoder>? encoderFactory = null, Func<Decoder, IUpdateDecoder>? decoderFactory = null)
    {
        var encoder = (encoderFactory ?? (() => new DSEncoderV2()))();
        var updateDecoder = new LazyStructReader((decoderFactory ?? V2Decoder)(new Decoder(update)), false);
        var curr = updateDecoder.Curr;
        if (curr != null)
        {
            int size = 0;
            long currClient = curr.Id.Client;
            bool stopCounting = curr.Id.Clock != 0; // must start at 0
            long currClock = stopCounting ? 0 : curr.Id.Clock + curr.Length;
            for (; curr != null; curr = updateDecoder.Next())
            {
                if (currClient != curr.Id.Client)
                {
                    if (currClock != 0)
                    {
                        size++;
                        // We found a new client
                        // write what we have to the encoder
                        encoder.RestEncoder.WriteVarUint(currClient);
                        encoder.RestEncoder.WriteVarUint(currClock);
                    }
                    currClient = curr.Id.Client;
                    currClock = 0;
                    stopCounting = curr.Id.Clock != 0;
                }
                // we ignore skips
                if (curr is Skip) stopCounting = true;
                if (!stopCounting) currClock = curr.Id.Clock + curr.Length;
            }
            // write what we have
            if (currClock != 0)
            {
                size++;
                encoder.RestEncoder.WriteVarUint(currClient);
                encoder.RestEncoder.WriteVarUint(currClock);
            }
            // prepend the size of the state vector
            var enc = new Encoder();
            enc.WriteVarUint(size);
            enc.WriteUint8Array(encoder.RestEncoder.AsSpan());
            return enc.ToArray();
        }
        encoder.RestEncoder.WriteVarUint(0);
        return encoder.ToArray();
    }

    public static byte[] EncodeStateVectorFromUpdate(byte[] update) => EncodeStateVectorFromUpdateV2(update, () => new DSEncoderV1(), V1Decoder);

    /// <summary>Returns, per client, the clock range (from, to) covered by the update.</summary>
    public static (Dictionary<long, long> from, Dictionary<long, long> to) ParseUpdateMetaV2(byte[] update, Func<Decoder, IUpdateDecoder>? decoderFactory = null)
    {
        var from = new Dictionary<long, long>();
        var to = new Dictionary<long, long>();
        var updateDecoder = new LazyStructReader((decoderFactory ?? V2Decoder)(new Decoder(update)), false);
        var curr = updateDecoder.Curr;
        if (curr != null)
        {
            long currClient = curr.Id.Client;
            long currClock = curr.Id.Clock;
            // write the beginning to `from`
            from[currClient] = currClock;
            for (; curr != null; curr = updateDecoder.Next())
            {
                if (currClient != curr.Id.Client)
                {
                    // We found a new client
                    // write the end to `to`
                    to[currClient] = currClock;
                    // write the beginning to `from`
                    from[curr.Id.Client] = curr.Id.Clock;
                    // update currClient
                    currClient = curr.Id.Client;
                }
                currClock = curr.Id.Clock + curr.Length;
            }
            // write the end to `to`
            to[currClient] = currClock;
        }
        return (from, to);
    }

    public static (Dictionary<long, long> from, Dictionary<long, long> to) ParseUpdateMeta(byte[] update) => ParseUpdateMetaV2(update, V1Decoder);

    /// <summary>This method is intended to slice any kind of struct and retrieve the right part. It does not handle side-effects, so it should only be used by the lazy-encoder.</summary>
    private static AbstractStruct SliceStruct(AbstractStruct left, long diff)
    {
        switch (left)
        {
            case GC:
                return new GC(new ID(left.Id.Client, left.Id.Clock + diff), left.Length - diff);
            case Skip:
                return new Skip(new ID(left.Id.Client, left.Id.Clock + diff), left.Length - diff);
            default:
                {
                    var leftItem = (Item)left;
                    var client = leftItem.Id.Client;
                    var clock = leftItem.Id.Clock;
                    return new Item(
                        new ID(client, clock + diff),
                        null,
                        new ID(client, clock + diff - 1),
                        null,
                        leftItem.RightOrigin,
                        leftItem.Parent,
                        leftItem.ParentSub,
                        leftItem.Content.Splice(diff));
                }
        }
    }

    /// <summary>
    /// Merge multiple updates into one. The resulting update is in the same format as the input.
    /// Updates may contain Skip structs if they do not describe a contiguous range of changes.
    /// </summary>
    public static byte[] MergeUpdatesV2(IReadOnlyList<byte[]> updates, Func<Decoder, IUpdateDecoder>? decoderFactory = null, Func<IUpdateEncoder>? encoderFactory = null)
    {
        if (updates.Count == 1) return updates[0];
        decoderFactory ??= V2Decoder;
        encoderFactory ??= V2Encoder;
        var updateDecoders = updates.Select(u => decoderFactory(new Decoder(u))).ToList();
        var lazyStructDecoders = updateDecoders.Select(d => new LazyStructReader(d, true)).ToList();

        // Note: We need to ensure that all lazyStructDecoders are fully consumed
        // Note: Should merge document updates whenever possible - even from different updates
        // Note: Should handle that some operations cannot be applied yet ()

        (AbstractStruct Struct, long Offset)? currWrite = null;

        var updateEncoder = encoderFactory();
        // write structs lazily
        var lazyStructEncoder = new LazyStructWriter(updateEncoder);

        // Note: we don't need to filter smaller clients because we are only interested in the sorting order.
        // Note: this is the same as the lazy encoder
        while (true)
        {
            // Write higher clients first ⇒ sort by clientID & clock and remove decoders without content
            lazyStructDecoders = lazyStructDecoders.Where(dec => dec.Curr != null).ToList();
            lazyStructDecoders.Sort((dec1, dec2) =>
            {
                if (dec1.Curr!.Id.Client == dec2.Curr!.Id.Client)
                {
                    long clockDiff = dec1.Curr.Id.Clock - dec2.Curr.Id.Clock;
                    if (clockDiff == 0)
                    {
                        // @todo remove references to skip since the structDecoders must filter Skips.
                        return dec1.Curr.GetType() == dec2.Curr.GetType()
                            ? 0
                            : dec1.Curr is Skip ? 1 : -1; // we are filtering skips anyway.
                    }
                    return clockDiff < 0 ? -1 : 1;
                }
                return dec2.Curr.Id.Client.CompareTo(dec1.Curr.Id.Client);
            });
            if (lazyStructDecoders.Count == 0) break;
            var currDecoder = lazyStructDecoders[0];
            // write from currDecoder until the next operation is from another client or if filler-struct
            // then we need to reorder the decoders and find the next operation to write
            long firstClient = currDecoder.Curr!.Id.Client;

            if (currWrite != null)
            {
                var curr = currDecoder.Curr;
                bool iterated = false;

                // iterate until we find something that we haven't written already
                // remember: first the high client-ids are written
                while (curr != null && curr.Id.Clock + curr.Length <= currWrite.Value.Struct.Id.Clock + currWrite.Value.Struct.Length && curr.Id.Client >= currWrite.Value.Struct.Id.Client)
                {
                    curr = currDecoder.Next();
                    iterated = true;
                }
                if (
                    curr == null || // current decoder is empty
                    curr.Id.Client != firstClient || // check whether there is another decoder that has has updates from `firstClient`
                    (iterated && curr.Id.Clock > currWrite.Value.Struct.Id.Clock + currWrite.Value.Struct.Length) // the above while loop was used and we are potentially missing updates
                )
                {
                    continue;
                }

                if (firstClient != currWrite.Value.Struct.Id.Client)
                {
                    WriteStructToLazyStructWriter(lazyStructEncoder, currWrite.Value.Struct, currWrite.Value.Offset);
                    currWrite = (curr, 0);
                    currDecoder.Next();
                }
                else
                {
                    if (currWrite.Value.Struct.Id.Clock + currWrite.Value.Struct.Length < curr.Id.Clock)
                    {
                        // @todo write currStruct & set currStruct = Skip(clock = currStruct.id.clock + currStruct.length, length = curr.id.clock - self.clock)
                        if (currWrite.Value.Struct is Skip skip)
                        {
                            // extend existing skip
                            skip.Length = curr.Id.Clock + curr.Length - skip.Id.Clock;
                        }
                        else
                        {
                            WriteStructToLazyStructWriter(lazyStructEncoder, currWrite.Value.Struct, currWrite.Value.Offset);
                            long diff = curr.Id.Clock - currWrite.Value.Struct.Id.Clock - currWrite.Value.Struct.Length;
                            var s = new Skip(new ID(firstClient, currWrite.Value.Struct.Id.Clock + currWrite.Value.Struct.Length), diff);
                            currWrite = (s, 0);
                        }
                    }
                    else
                    {
                        // if (currWrite.struct.id.clock + currWrite.struct.length >= curr.id.clock) {
                        long diff = currWrite.Value.Struct.Id.Clock + currWrite.Value.Struct.Length - curr.Id.Clock;
                        if (diff > 0)
                        {
                            if (currWrite.Value.Struct is Skip skip)
                            {
                                // prefer to slice Skip because the other struct might contain more information
                                skip.Length -= diff;
                            }
                            else
                            {
                                curr = SliceStruct(curr, diff);
                            }
                        }
                        if (!currWrite.Value.Struct.MergeWith(curr))
                        {
                            WriteStructToLazyStructWriter(lazyStructEncoder, currWrite.Value.Struct, currWrite.Value.Offset);
                            currWrite = (curr, 0);
                            currDecoder.Next();
                        }
                    }
                }
            }
            else
            {
                currWrite = (currDecoder.Curr, 0);
                currDecoder.Next();
            }
            for (
                var next = currDecoder.Curr;
                next != null && next.Id.Client == firstClient && next.Id.Clock == currWrite.Value.Struct.Id.Clock + currWrite.Value.Struct.Length && next is not Skip;
                next = currDecoder.Next()
            )
            {
                WriteStructToLazyStructWriter(lazyStructEncoder, currWrite.Value.Struct, currWrite.Value.Offset);
                currWrite = (next, 0);
            }
        }
        if (currWrite != null)
        {
            WriteStructToLazyStructWriter(lazyStructEncoder, currWrite.Value.Struct, currWrite.Value.Offset);
            currWrite = null;
        }
        FinishLazyStructWriting(lazyStructEncoder);

        var dss = updateDecoders.Select(DeleteSet.Read).ToList();
        var ds = DeleteSet.Merge(dss);
        DeleteSet.Write(updateEncoder, ds);
        return updateEncoder.ToArray();
    }

    /// <summary>Computes the part of <paramref name="update"/> that is not covered by the state vector <paramref name="sv"/>.</summary>
    public static byte[] DiffUpdateV2(byte[] update, byte[] sv, Func<Decoder, IUpdateDecoder>? decoderFactory = null, Func<IUpdateEncoder>? encoderFactory = null)
    {
        var state = UpdateCodec.DecodeStateVector(sv);
        var encoder = (encoderFactory ?? V2Encoder)();
        var lazyStructWriter = new LazyStructWriter(encoder);
        var decoder = (decoderFactory ?? V2Decoder)(new Decoder(update));
        var reader = new LazyStructReader(decoder, false);
        while (reader.Curr != null)
        {
            var curr = reader.Curr;
            long currClient = curr.Id.Client;
            long svClock = state.GetValueOrDefault(currClient);
            if (reader.Curr is Skip)
            {
                // the first written struct shouldn't be a skip
                reader.Next();
                continue;
            }
            if (curr.Id.Clock + curr.Length > svClock)
            {
                WriteStructToLazyStructWriter(lazyStructWriter, curr, Math.Max(svClock - curr.Id.Clock, 0));
                reader.Next();
                while (reader.Curr != null && reader.Curr.Id.Client == currClient)
                {
                    WriteStructToLazyStructWriter(lazyStructWriter, reader.Curr, 0);
                    reader.Next();
                }
            }
            else
            {
                // read until something new comes up
                while (reader.Curr != null && reader.Curr.Id.Client == currClient && reader.Curr.Id.Clock + reader.Curr.Length <= svClock)
                {
                    reader.Next();
                }
            }
        }
        FinishLazyStructWriting(lazyStructWriter);
        // write ds
        var ds = DeleteSet.Read(decoder);
        DeleteSet.Write(encoder, ds);
        return encoder.ToArray();
    }

    public static byte[] DiffUpdate(byte[] update, byte[] sv) => DiffUpdateV2(update, sv, V1Decoder, V1Encoder);

    private static void FlushLazyStructWriter(LazyStructWriter lazyWriter)
    {
        if (lazyWriter.Written > 0)
        {
            lazyWriter.ClientStructs.Add((lazyWriter.Written, lazyWriter.Encoder.RestEncoder.ToArray()));
            ResetRestEncoder(lazyWriter.Encoder);
            lazyWriter.Written = 0;
        }
    }

    private static void ResetRestEncoder(IUpdateEncoder encoder)
    {
        switch (encoder)
        {
            case DSEncoderV1 v1: v1.RestEncoder = new Encoder(); break;
            case DSEncoderV2 v2: v2.RestEncoder = new Encoder(); break;
            default: throw new InvalidOperationException("Unknown encoder");
        }
    }

    private static void WriteStructToLazyStructWriter(LazyStructWriter lazyWriter, AbstractStruct s, long offset)
    {
        // flush curr if we start another client
        if (lazyWriter.Written > 0 && lazyWriter.CurrClient != s.Id.Client) FlushLazyStructWriter(lazyWriter);
        if (lazyWriter.Written == 0)
        {
            lazyWriter.CurrClient = s.Id.Client;
            // write next client
            lazyWriter.Encoder.WriteClient(s.Id.Client);
            // write startClock
            lazyWriter.Encoder.RestEncoder.WriteVarUint(s.Id.Clock + offset);
        }
        s.Write(lazyWriter.Encoder, offset);
        lazyWriter.Written++;
    }

    /// <summary>Call this function when we collected all parts and want to write the update.</summary>
    private static void FinishLazyStructWriting(LazyStructWriter lazyWriter)
    {
        FlushLazyStructWriter(lazyWriter);

        // this is a fresh encoder because we called flushCurr
        var restEncoder = lazyWriter.Encoder.RestEncoder;

        // Now we put all the fragments together.
        // This works similarly to `writeClientsStructs`

        // write # states that were updated - i.e. the clients
        restEncoder.WriteVarUint(lazyWriter.ClientStructs.Count);

        foreach (var (written, bytes) in lazyWriter.ClientStructs)
        {
            // write # encoded structs
            restEncoder.WriteVarUint(written);
            // write the rest of the fragment
            restEncoder.WriteUint8Array(bytes);
        }
    }

    public static byte[] ConvertUpdateFormat(byte[] update, Func<AbstractStruct, AbstractStruct> blockTransformer, Func<Decoder, IUpdateDecoder> decoderFactory, Func<IUpdateEncoder> encoderFactory)
    {
        var updateDecoder = decoderFactory(new Decoder(update));
        var lazyDecoder = new LazyStructReader(updateDecoder, false);
        var updateEncoder = encoderFactory();
        var lazyWriter = new LazyStructWriter(updateEncoder);
        for (var curr = lazyDecoder.Curr; curr != null; curr = lazyDecoder.Next())
        {
            WriteStructToLazyStructWriter(lazyWriter, blockTransformer(curr), 0);
        }
        FinishLazyStructWriting(lazyWriter);
        var ds = DeleteSet.Read(updateDecoder);
        DeleteSet.Write(updateEncoder, ds);
        return updateEncoder.ToArray();
    }

    public static byte[] ConvertUpdateFormatV1ToV2(byte[] update) => ConvertUpdateFormat(update, s => s, V1Decoder, V2Encoder);

    public static byte[] ConvertUpdateFormatV2ToV1(byte[] update) => ConvertUpdateFormat(update, s => s, V2Decoder, V1Encoder);
}
