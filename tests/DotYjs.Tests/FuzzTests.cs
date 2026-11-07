using DotYjs.AspNetCore;
using DotYjs.Lib0;
using DotYjs.Protocols;

namespace DotYjs.Tests;

/// <summary>
/// Mutation fuzzing of every entry point that decodes bytes from the network or from storage.
/// Valid messages from the JS fixtures are mutated (bit flips, random bytes, insertions, deletions, truncation,
/// huge varints) and fed to the decoders. For any input, a decoder must either succeed or throw
/// <see cref="InvalidDataException"/>; it must not hang, allocate unbounded memory or throw anything else.
/// The number of mutations per entry point can be raised with the DOTYJS_FUZZ_ITERATIONS environment variable,
/// and DOTYJS_FUZZ_SEED picks another sequence of mutations.
/// </summary>
public class FuzzTests
{
    private const long MaxAllocatedBytes = 256L * 1024 * 1024;
    private static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(10);

    private static readonly int Iterations = int.TryParse(Environment.GetEnvironmentVariable("DOTYJS_FUZZ_ITERATIONS"), out var n) ? n : 300;
    private static readonly int Seed = int.TryParse(Environment.GetEnvironmentVariable("DOTYJS_FUZZ_SEED"), out var s) ? s : 20261107;

    private static byte[] B64(string s) => Convert.FromBase64String(s);

    private static readonly List<byte[]> UpdatesV1 = LoadUpdates("updateV1", withIncremental: true);
    private static readonly List<byte[]> UpdatesV2 = LoadUpdates("updateV2", withIncremental: false);
    private static readonly List<byte[]> StateVectors = UpdatesV1.Take(5).Select(Updates.EncodeStateVectorFromUpdate).ToList();
    private static readonly List<byte[]> SyncMessages = ProtocolFixtures("syncStep1", "syncStep2", "syncUpdate", "syncStep1Reply");
    private static readonly List<byte[]> AwarenessUpdates = ProtocolFixtures("awarenessUpdate", "awarenessAll", "awarenessRemoved");
    private static readonly List<byte[]> RelativePositions = InteropTests.Fixture("protocols").GetProperty("relativePositions")
        .EnumerateArray().Select(e => B64(e.GetProperty("encoded").GetString()!)).ToList();

    private static List<byte[]> LoadUpdates(string property, bool withIncremental)
    {
        var result = new List<byte[]>();
        foreach (var name in new[] { "text_basic", "text_formatting", "map_values", "array_nested", "xml", "many_ops" })
        {
            var f = InteropTests.Fixture(name);
            result.Add(B64(f.GetProperty(property).GetString()!));
            if (withIncremental) result.AddRange(f.GetProperty("incrementalUpdates").EnumerateArray().Take(20).Select(u => B64(u.GetString()!)));
        }
        return result;
    }

    private static List<byte[]> ProtocolFixtures(params string[] names) =>
        names.Select(k => B64(InteropTests.Fixture("protocols").GetProperty(k).GetString()!)).ToList();

    private static Doc DocWithContent()
    {
        var doc = new Doc();
        UpdateCodec.ApplyUpdate(doc, UpdatesV1[0]);
        return doc;
    }

    /// <summary>The entry points: name, valid seed inputs and the code that decodes an input.</summary>
    public static TheoryData<string> Targets() => new(TargetActions.Keys);

    private static readonly Dictionary<string, (Func<List<byte[]>> Seeds, Action<byte[]> Run)> TargetActions = new(StringComparer.Ordinal)
    {
        ["ApplyUpdate"] = (() => UpdatesV1, b => UpdateCodec.ApplyUpdate(new Doc(), b)),
        ["ApplyUpdateV2"] = (() => UpdatesV2, b => UpdateCodec.ApplyUpdateV2(new Doc(), b)),
        ["ApplyUpdate to a document with content"] = (() => UpdatesV1, ApplyToExistingDocument),
        ["DecodeUpdate"] = (() => UpdatesV1, b => Updates.DecodeUpdate(b)),
        ["DecodeUpdateV2"] = (() => UpdatesV2, b => Updates.DecodeUpdateV2(b)),
        ["MergeUpdates"] = (() => UpdatesV1, b => Updates.MergeUpdates(new[] { b, UpdatesV1[1] })),
        ["MergeUpdatesV2"] = (() => UpdatesV2, b => Updates.MergeUpdatesV2(new[] { b, UpdatesV2[1] })),
        ["DiffUpdate"] = (() => UpdatesV1, b => Updates.DiffUpdate(b, StateVectors[0])),
        ["DiffUpdate with a corrupt state vector"] = (() => StateVectors, b => Updates.DiffUpdate(UpdatesV1[0], b)),
        ["ConvertUpdateFormatV1ToV2"] = (() => UpdatesV1, b => Updates.ConvertUpdateFormatV1ToV2(b)),
        ["ConvertUpdateFormatV2ToV1"] = (() => UpdatesV2, ConvertV2ToV1),
        ["EncodeStateVectorFromUpdate"] = (() => UpdatesV1, b => Updates.EncodeStateVectorFromUpdate(b)),
        ["ParseUpdateMeta"] = (() => UpdatesV1, b => Updates.ParseUpdateMeta(b)),
        ["DecodeStateVector"] = (() => StateVectors, b => UpdateCodec.DecodeStateVector(b)),
        ["EncodeStateAsUpdate with a corrupt state vector"] = (() => StateVectors, b => UpdateCodec.EncodeStateAsUpdate(DocWithContent(), b)),
        ["SyncProtocol.ReadSyncMessage"] = (() => SyncMessages, b => SyncProtocol.ReadSyncMessage(new Decoder(b), new Encoder(), DocWithContent())),
        ["Awareness.ApplyUpdate"] = (() => AwarenessUpdates, ApplyAwareness),
        ["RelativePosition.Decode"] = (() => RelativePositions, b => RelativePosition.Decode(b)),
        ["Snapshot.Decode"] = (() => new List<byte[]> { new Snapshot(new DeleteSet(), new Dictionary<long, long> { [1] = 5 }).Encode() }, b => Snapshot.Decode(b)),
        ["Decoder.ReadAny"] = (() => AwarenessUpdates, ReadAllAny),
        ["YjsBackplaneMessage.Decode"] = (() => new List<byte[]> { new YjsBackplaneMessage(YjsBackplaneMessageKind.DocumentUpdate, UpdatesV1[0], "node").Encode() }, b => YjsBackplaneMessage.Decode(b)),
    };

    private static void ApplyToExistingDocument(byte[] update)
    {
        var doc = DocWithContent();
        try
        {
            UpdateCodec.ApplyUpdate(doc, update);
        }
        finally
        {
            // whether or not the update was rejected, the document must stay usable
            var state = UpdateCodec.EncodeStateAsUpdate(doc);
            UpdateCodec.ApplyUpdate(new Doc(), state);
            UpdateCodec.ApplyUpdate(doc, UpdatesV1[1]);
        }
    }

    private static void ConvertV2ToV1(byte[] update)
    {
        try
        {
            Updates.ConvertUpdateFormatV2ToV1(update);
        }
        catch (ArgumentException e) when (e.Message.Contains("BigInt", StringComparison.Ordinal))
        {
            // v2 stores embeds in the any encoding, v1 as JSON: a BigInt embed cannot be converted (like in yjs)
        }
    }

    private static void ApplyAwareness(byte[] update)
    {
        using var awareness = new Awareness(new Doc(), runOutdatedCheck: false);
        awareness.ApplyUpdate(update, "remote");
    }

    private static void ReadAllAny(byte[] data)
    {
        var decoder = new Decoder(data);
        while (decoder.HasContent) decoder.ReadAny();
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void Corrupt_Input_Is_Rejected_With_InvalidDataException(string target)
    {
        var (seeds, run) = TargetActions[target];
        var inputs = seeds();
        var random = new Random(HashCode.Combine(Seed, StableHash(target)));
        for (int i = 0; i < Iterations; i++)
        {
            var input = Mutate(inputs[random.Next(inputs.Count)], random);
            var failure = RunGuarded(run, input);
            if (failure != null) Assert.Fail($"{target}, input {Convert.ToBase64String(input)}: {failure}");
        }
    }

    private static int StableHash(string s)
    {
        int h = 17;
        foreach (var c in s) h = (h * 31) + c;
        return h;
    }

    /// <summary>Runs the decoder on its own thread; returns a description of the failure, or null if the contract holds.</summary>
    private static string? RunGuarded(Action<byte[]> run, byte[] input)
    {
        Exception? error = null;
        long allocated = 0;
        var thread = new Thread(() =>
        {
            long before = System.GC.GetAllocatedBytesForCurrentThread();
            try
            {
                run(input);
            }
            catch (Exception e)
            {
                error = e;
            }
            allocated = System.GC.GetAllocatedBytesForCurrentThread() - before;
        }, 16 * 1024 * 1024)
        { IsBackground = true };
        thread.Start();
        if (!thread.Join(MaxDuration)) return $"did not finish within {MaxDuration.TotalSeconds} s";
        if (allocated > MaxAllocatedBytes) return $"allocated {allocated / (1024 * 1024)} MB";
        return error is null or InvalidDataException ? null : error.ToString();
    }

    private static readonly byte[] InterestingBytes = { 0, 1, 0x3f, 0x40, 0x7f, 0x80, 0xff, 116, 117, 118, 119 };

    private static byte[] Mutate(byte[] seed, Random random)
    {
        var b = new List<byte>(seed);
        int mutations = 1 + random.Next(4);
        for (int k = 0; k < mutations; k++)
        {
            switch (random.Next(8))
            {
                case 0 when b.Count > 0:
                    b[random.Next(b.Count)] ^= (byte)(1 << random.Next(8));
                    break;
                case 1 when b.Count > 0:
                    b[random.Next(b.Count)] = (byte)random.Next(256);
                    break;
                case 2:
                    b.Insert(random.Next(b.Count + 1), (byte)random.Next(256));
                    break;
                case 3 when b.Count > 0:
                    b.RemoveAt(random.Next(b.Count));
                    break;
                case 4 when b.Count > 0:
                    {
                        int cut = random.Next(b.Count);
                        b.RemoveRange(cut, b.Count - cut);
                        break;
                    }
                case 5:
                    b.InsertRange(random.Next(b.Count + 1), new byte[] { 0xff, 0xff, 0xff, 0xff, 0x07 }); // var uint 2^31 - 1
                    break;
                case 6 when b.Count > 0:
                    b[random.Next(b.Count)] = InterestingBytes[random.Next(InterestingBytes.Length)];
                    break;
                case 7:
                    b.InsertRange(random.Next(b.Count + 1), new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x7f }); // var uint beyond 2^53
                    break;
            }
        }
        return b.ToArray();
    }

    // ---- regressions for issues the fuzzer found ----

    [Fact]
    public void Huge_Lengths_Do_Not_Reserve_Memory()
    {
        // any-encoded array (117) and object (118) claiming 2^31 - 1 entries
        foreach (var tag in new byte[] { 117, 118 })
        {
            var failure = RunGuarded(b => new Decoder(b).ReadAny(), new byte[] { tag, 0xff, 0xff, 0xff, 0xff, 0x07, 0x7e });
            Assert.Null(failure);
        }
        // v1 update claiming 2^31 - 1 structs for one client
        Assert.Null(RunGuarded(b => UpdateCodec.ApplyUpdate(new Doc(), b), new byte[] { 1, 0xff, 0xff, 0xff, 0xff, 0x07, 1, 0 }));
    }

    [Fact]
    public void Integers_Beyond_MaxSafeInteger_Are_Rejected()
    {
        var bytes = new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x7f }; // 2^56 - 1
        Assert.Throws<InvalidDataException>(() => new Decoder(bytes).ReadVarUint());
        var max = new Encoder();
        max.WriteVarUint((1L << 53) - 1);
        Assert.Equal((1L << 53) - 1, new Decoder(max.ToArray()).ReadVarUint());
    }

    [Fact]
    public void Item_Referencing_A_Later_Clock_Of_Its_Own_Client_Is_Rejected_Before_Anything_Is_Applied()
    {
        // client 1, clock 0: an item whose left origin is (1, 5), which cannot exist before it
        var e = new UpdateEncoderV1();
        e.RestEncoder.WriteVarUint(1); // one client
        e.RestEncoder.WriteVarUint(1); // one struct
        e.WriteClient(1);
        e.RestEncoder.WriteVarUint(0); // clock
        e.WriteInfo(0x80 | 4); // has origin, string content
        e.WriteLeftID(new ID(1, 5));
        e.WriteString("x");
        e.RestEncoder.WriteVarUint(0); // empty delete set
        var doc = DocWithContent();
        var before = UpdateCodec.EncodeStateAsUpdate(doc);
        Assert.Throws<InvalidDataException>(() => UpdateCodec.ApplyUpdate(doc, e.ToArray()));
        Assert.Equal(before, UpdateCodec.EncodeStateAsUpdate(doc));
    }

    [Fact]
    public void Unsorted_Delete_Set_Is_Rejected()
    {
        var e = new UpdateEncoderV1();
        e.RestEncoder.WriteVarUint(0); // no structs
        e.RestEncoder.WriteVarUint(1); // one client in the delete set
        e.RestEncoder.WriteVarUint(1); // client
        e.RestEncoder.WriteVarUint(2); // two ranges, in the wrong order
        e.WriteDsClock(10);
        e.WriteDsLen(2);
        e.WriteDsClock(3);
        e.WriteDsLen(2);
        Assert.Throws<InvalidDataException>(() => Updates.ConvertUpdateFormatV1ToV2(e.ToArray()));
        Assert.Throws<InvalidDataException>(() => UpdateCodec.ApplyUpdate(new Doc(), e.ToArray()));
    }

    [Fact]
    public void Invalid_Json_In_Awareness_Is_InvalidDataException()
    {
        var e = new Encoder();
        e.WriteVarUint(1); // one client
        e.WriteVarUint(42); // client id
        e.WriteVarUint(1); // clock
        e.WriteVarString("{not json");
        using var awareness = new Awareness(new Doc(), runOutdatedCheck: false);
        Assert.Throws<InvalidDataException>(() => awareness.ApplyUpdate(e.ToArray(), "remote"));
    }

    [Fact]
    public void Valid_Fixtures_Still_Decode()
    {
        // the checks added for corrupt input must not reject anything yjs produces
        foreach (var update in UpdatesV1) UpdateCodec.ApplyUpdate(new Doc(), update);
        foreach (var update in UpdatesV2) UpdateCodec.ApplyUpdateV2(new Doc(), update);
        foreach (var message in AwarenessUpdates)
        {
            using var awareness = new Awareness(new Doc(), runOutdatedCheck: false);
            awareness.ApplyUpdate(message, "remote");
        }
        foreach (var position in RelativePositions) Assert.NotNull(RelativePosition.Decode(position));
    }
}
