using System.Text.Json;
using DotYjs.Lib0;
using DotYjs.Protocols;

namespace DotYjs.Tests;

public class ProtocolTests
{
    private static Dictionary<string, object?> Obj(params (string, object?)[] kv) => kv.ToDictionary(x => x.Item1, x => x.Item2);

    [Fact]
    public void Sync_Handshake_Converges_Two_Docs()
    {
        var a = new Doc();
        var b = new Doc();
        a.GetText("t").Insert(0, "from a");
        b.GetText("t").Insert(0, "from b");

        // a → b: step1 ; b → a: step2 + its own step1 ; a → b: step2
        var step1A = SyncProtocol.EncodeSyncStep1(a);
        var replyB = new Encoder();
        Assert.Equal(SyncProtocol.MessageYjsSyncStep1, SyncProtocol.ReadSyncMessage(new Decoder(step1A), replyB, b, "remote"));
        SyncProtocol.WriteSyncStep1(replyB, b);
        var dec = new Decoder(replyB.ToArray());
        var replyA = new Encoder();
        Assert.Equal(SyncProtocol.MessageYjsSyncStep2, SyncProtocol.ReadSyncMessage(dec, replyA, a, "remote"));
        Assert.Equal(SyncProtocol.MessageYjsSyncStep1, SyncProtocol.ReadSyncMessage(dec, replyA, a, "remote"));
        Assert.False(dec.HasContent);
        var ignored = new Encoder();
        Assert.Equal(SyncProtocol.MessageYjsSyncStep2, SyncProtocol.ReadSyncMessage(new Decoder(replyA.ToArray()), ignored, b, "remote"));
        Assert.False(ignored.HasContent);

        Assert.Equal(a.GetText("t").ToString(), b.GetText("t").ToString());
        Assert.Equal(UpdateCodec.EncodeStateVector(a), UpdateCodec.EncodeStateVector(b));

        // incremental update message
        object? origin = null;
        b.Update += (_, o, _, _) => origin = o;
        a.GetText("t").Insert(0, "!");
        var upd = SyncProtocol.EncodeUpdate(UpdateCodec.EncodeStateAsUpdate(a, UpdateCodec.EncodeStateVector(b)));
        SyncProtocol.ReadSyncMessage(new Decoder(upd), new Encoder(), b, "ws");
        Assert.Equal("ws", origin);
        Assert.Equal(a.GetText("t").ToString(), b.GetText("t").ToString());
        Assert.Throws<InvalidDataException>(() => SyncProtocol.ReadSyncMessage(new Decoder(new byte[] { 9 }), new Encoder(), b));
    }

    [Fact]
    public void Awareness_Local_State_And_Events()
    {
        var doc = new Doc();
        using var aw = new Awareness(doc, runOutdatedCheck: false);
        Assert.Equal(doc.ClientID, aw.ClientID);
        Assert.IsType<Dictionary<string, object?>>(aw.GetLocalState());
        var changes = new List<(AwarenessChange c, object? o)>();
        var updates = new List<AwarenessChange>();
        aw.Change += (c, o) => changes.Add((c, o));
        aw.Update += (c, _) => updates.Add(c);

        aw.SetLocalState(Obj(("name", "x")));
        Assert.Single(changes);
        Assert.Equal(new[] { doc.ClientID }, changes[0].c.Updated);
        Assert.Equal("local", changes[0].o);
        // same content: update fires, change does not
        aw.SetLocalState(Obj(("name", "x")));
        Assert.Single(changes);
        Assert.Equal(2, updates.Count);
        aw.SetLocalStateField("cursor", 3L);
        Assert.Equal(2, changes.Count);
        Assert.Equal(3L, ((Dictionary<string, object?>)aw.GetLocalState()!)["cursor"]);
        Assert.Equal(3, aw.GetMeta()[doc.ClientID].Clock);
        aw.SetLocalState(null);
        Assert.Equal(new[] { doc.ClientID }, changes[^1].c.Removed);
        Assert.Null(aw.GetLocalState());
        Assert.Empty(aw.GetStates());
    }

    [Fact]
    public void Awareness_Apply_Update_Between_Peers()
    {
        var d1 = new Doc { ClientID = 1 };
        var d2 = new Doc { ClientID = 2 };
        using var a1 = new Awareness(d1, false);
        using var a2 = new Awareness(d2, false);
        a1.SetLocalState(Obj(("user", "one")));
        AwarenessChange? change = null;
        object? origin = null;
        a2.Change += (c, o) => { change = c; origin = o; };
        a2.ApplyUpdate(a1.EncodeUpdate(new[] { 1L }), "conn");
        Assert.Equal(new[] { 1L }, change!.Added);
        Assert.Equal("conn", origin);
        Assert.Equal("one", ((Dictionary<string, object?>)a2.GetStates()[1]!)["user"]);

        // stale clock is ignored
        var old = a1.EncodeUpdate(new[] { 1L });
        a1.SetLocalState(Obj(("user", "one-updated")));
        a2.ApplyUpdate(a1.EncodeUpdate(new[] { 1L }), "conn");
        Assert.Equal(new[] { 1L }, change!.Updated);
        a2.ApplyUpdate(old, "conn");
        Assert.Equal("one-updated", ((Dictionary<string, object?>)a2.GetStates()[1]!)["user"]);

        // removal
        a1.SetLocalState(null);
        a2.ApplyUpdate(a1.EncodeUpdate(new[] { 1L }), "conn");
        Assert.Equal(new[] { 1L }, change!.Removed);
        Assert.False(a2.GetStates().ContainsKey(1));

        // a remote peer can never remove our own local state: the clock is bumped instead
        var a2Clock = a2.GetMeta()[2].Clock;
        var removeTwo = a2.EncodeUpdate(new[] { 2L }, new Dictionary<long, object?>()); // state null
        a2.ApplyUpdate(removeTwo, "conn");
        Assert.NotNull(a2.GetLocalState());
        Assert.Equal(a2Clock + 1, a2.GetMeta()[2].Clock);

        // RemoveStates (what the server does when a connection closes)
        a1.SetLocalState(Obj(("x", 1L)));
        a2.ApplyUpdate(a1.EncodeUpdate(new[] { 1L }), "conn");
        Assert.True(a2.GetStates().ContainsKey(1));
        a2.RemoveStates(new[] { 1L }, "closed");
        Assert.Equal("closed", origin);
        Assert.False(a2.GetStates().ContainsKey(1));

        // modify
        var modified = Awareness.ModifyUpdate(a2.EncodeUpdate(new[] { 2L }), s => Obj(("replaced", true)));
        var dec = new Decoder(modified);
        Assert.Equal(1, dec.ReadVarUint());
        Assert.Equal(2, dec.ReadVarUint());
        dec.ReadVarUint();
        Assert.Equal("{\"replaced\":true}", dec.ReadVarString());
    }

    [Fact]
    public void Awareness_CheckOutdated_Removes_Stale_Remote_And_Renews_Local()
    {
        var d = new Doc { ClientID = 1 };
        using var aw = new Awareness(d, false);
        // inject a remote state whose lastUpdated is "now" and verify it is kept
        using var remote = new Awareness(new Doc { ClientID = 2 }, false);
        // like in JS, the initial {} state has clock 0 and is never accepted by peers; a real state has clock >= 1
        aw.ApplyUpdate(remote.EncodeUpdate(new[] { 2L }), "r");
        Assert.False(aw.GetStates().ContainsKey(2));
        remote.SetLocalState(Obj(("name", "r")));
        aw.ApplyUpdate(remote.EncodeUpdate(new[] { 2L }), "r");
        aw.CheckOutdated();
        Assert.True(aw.GetStates().ContainsKey(2));
        Assert.Equal(0, aw.GetMeta()[1].Clock); // local clock untouched: not old enough to renew
    }

    [Fact]
    public void YWebSocket_Envelopes()
    {
        var doc = new Doc();
        var frame = YWebSocketProtocol.EncodeSyncStep1(doc);
        var dec = new Decoder(frame);
        Assert.Equal(YWebSocketProtocol.MessageSync, dec.ReadVarUint());
        Assert.Equal(SyncProtocol.MessageYjsSyncStep1, dec.ReadVarUint());
        Assert.Equal(UpdateCodec.EncodeStateVector(doc), dec.ReadVarUint8Array());
        Assert.Equal(new byte[] { 3 }, YWebSocketProtocol.EncodeQueryAwareness());
        var denied = new Decoder(YWebSocketProtocol.EncodePermissionDenied("nope"));
        Assert.Equal(2, denied.ReadVarUint());
        Assert.Equal(0, denied.ReadVarUint());
        Assert.Equal("nope", denied.ReadVarString());
    }

    [Fact]
    public void DeepEquals_Semantics()
    {
        Assert.True(JsonValues.DeepEquals(Obj(("a", 1L), ("b", new List<object?> { 1.0, "x" })), Obj(("b", new List<object?> { 1L, "x" }), ("a", 1.0))));
        Assert.False(JsonValues.DeepEquals(Obj(("a", 1L)), Obj(("a", 2L))));
        Assert.False(JsonValues.DeepEquals(Obj(("a", 1L)), Obj(("a", 1L), ("b", null))));
        Assert.True(JsonValues.DeepEquals(null, null));
        Assert.False(JsonValues.DeepEquals(null, Obj()));
    }

    // ---- interop with JS y-protocols (fixtures/js-fixtures.json → "protocols") ----

    private static JsonElement Fx => InteropTests.Fixture("protocols");
    private static byte[] B64(string name) => Convert.FromBase64String(Fx.GetProperty(name).GetString()!);

    private static (Doc doc, Doc peer) BuildProtocolDocs()
    {
        var doc = new Doc { ClientID = Fx.GetProperty("clientID").GetInt64() };
        var t = doc.GetText("t");
        t.Insert(0, "hello world");
        t.Insert(5, ",");
        var peer = new Doc { ClientID = Fx.GetProperty("peerClientID").GetInt64() };
        peer.GetText("t").Insert(0, "xyz");
        UpdateCodec.ApplyUpdate(peer, UpdateCodec.EncodeStateAsUpdate(doc, UpdateCodec.EncodeStateVector(peer)));
        return (doc, peer);
    }

    [Fact]
    public void Sync_Messages_Are_Byte_Identical_To_Js()
    {
        var (doc, peer) = BuildProtocolDocs();
        Assert.Equal(B64("docUpdate"), UpdateCodec.EncodeStateAsUpdate(doc));
        Assert.Equal(B64("peerUpdate"), UpdateCodec.EncodeStateAsUpdate(peer));
        Assert.Equal(B64("syncStep1"), SyncProtocol.EncodeSyncStep1(doc));
        Assert.Equal(B64("syncStep2"), SyncProtocol.EncodeSyncStep2(doc, UpdateCodec.EncodeStateVector(peer)));
        Assert.Equal(B64("syncUpdate"), SyncProtocol.EncodeUpdate(UpdateCodec.EncodeStateAsUpdate(peer, UpdateCodec.EncodeStateVector(doc))));

        // the reply y-protocols generated for JS peer must equal ours, and applying JS messages must work
        var reply = new Encoder();
        SyncProtocol.ReadSyncMessage(new Decoder(B64("syncStep1")), reply, peer, "test");
        Assert.Equal(B64("syncStep1Reply"), reply.ToArray());
        SyncProtocol.ReadSyncMessage(new Decoder(B64("syncStep1Reply")), new Encoder(), doc, "test");
        Assert.Equal(Fx.GetProperty("peerText").GetString(), doc.GetText("t").ToString());
        // syncStep2 was computed against peer's state vector, so it carries nothing new for a doc that already has docUpdate
        var fresh = new Doc();
        UpdateCodec.ApplyUpdate(fresh, B64("docUpdate"));
        SyncProtocol.ReadSyncMessage(new Decoder(B64("syncStep2")), new Encoder(), fresh, "test");
        SyncProtocol.ReadSyncMessage(new Decoder(B64("syncUpdate")), new Encoder(), fresh, "test");
        Assert.Equal(Fx.GetProperty("peerText").GetString(), fresh.GetText("t").ToString());
    }

    [Fact]
    public void Awareness_Updates_Are_Byte_Identical_To_Js()
    {
        var (doc, peer) = BuildProtocolDocs();
        using var aw = new Awareness(doc, false);
        var state = JsonValues.Parse(Fx.GetProperty("awarenessState").GetString()!);
        aw.SetLocalState(state);
        Assert.Equal(B64("awarenessUpdate"), aw.EncodeUpdate(new[] { doc.ClientID }));

        using var aw2 = new Awareness(peer, false);
        aw2.SetLocalState(Obj(("user", Obj(("name", "peer")))));
        aw2.ApplyUpdate(B64("awarenessUpdate"), "remote");
        var states = aw2.GetStates();
        Assert.Equal(2, states.Count);
        Assert.True(JsonValues.DeepEquals(state, states[doc.ClientID]));
        var expectedAll = (Dictionary<string, object?>)JsonValues.Parse(Fx.GetProperty("awarenessAllStates").GetString()!)!;
        foreach (var (k, v) in expectedAll) Assert.True(JsonValues.DeepEquals(v, states[long.Parse(k)]));
        Assert.Equal(B64("awarenessAll"), aw2.EncodeUpdate(states.Keys.OrderBy(x => x).ToList()));
        aw2.SetLocalState(null);
        Assert.Equal(B64("awarenessRemoved"), aw2.EncodeUpdate(new[] { peer.ClientID }));

        // applying JS "all" update to a third party yields the same states
        using var aw3 = new Awareness(new Doc { ClientID = 99 }, false);
        aw3.ApplyUpdate(B64("awarenessAll"), "remote");
        Assert.Equal(3, aw3.GetStates().Count);
        aw3.ApplyUpdate(B64("awarenessRemoved"), "remote");
        Assert.False(aw3.GetStates().ContainsKey(peer.ClientID));
    }

    [Fact]
    public void Relative_Positions_Are_Byte_Identical_To_Js()
    {
        var (doc, _) = BuildProtocolDocs();
        var t = doc.GetText("t");
        foreach (var rp in Fx.GetProperty("relativePositions").EnumerateArray())
        {
            var index = rp.GetProperty("index").GetInt64();
            var assoc = rp.GetProperty("assoc").GetInt64();
            var encoded = Convert.FromBase64String(rp.GetProperty("encoded").GetString()!);
            var ours = RelativePosition.FromTypeIndex(t, index, assoc);
            Assert.Equal(encoded, ours.Encode());
            Assert.Equal(rp.GetProperty("json").GetString(), JsonValues.Stringify(ours.ToJSON()));
            var abs = RelativePosition.Decode(encoded).ToAbsolutePosition(doc)!;
            Assert.Same(t, abs.Type);
            Assert.Equal(index, abs.Index);
            Assert.Equal(assoc, abs.Assoc);
        }
    }

    [Fact]
    public void Write_DotNet_Protocol_Fixtures_For_Js_Verification()
    {
        var (doc, peer) = BuildProtocolDocs();
        using var aw = new Awareness(doc, false);
        aw.SetLocalState(Obj(("user", Obj(("name", "dotnet"))), ("cursor", Obj(("anchor", 1L), ("head", 2.5)))));
        var result = new Dictionary<string, object?>
        {
            ["clientID"] = doc.ClientID,
            ["syncStep1"] = Convert.ToBase64String(SyncProtocol.EncodeSyncStep1(doc)),
            ["syncStep2"] = Convert.ToBase64String(SyncProtocol.EncodeSyncStep2(doc)),
            ["syncUpdate"] = Convert.ToBase64String(SyncProtocol.EncodeUpdate(UpdateCodec.EncodeStateAsUpdate(peer, UpdateCodec.EncodeStateVector(doc)))),
            ["text"] = doc.GetText("t").ToString(),
            ["peerText"] = peer.GetText("t").ToString(),
            ["awarenessUpdate"] = Convert.ToBase64String(aw.EncodeUpdate(new[] { doc.ClientID })),
            ["awarenessState"] = JsonValues.Stringify(aw.GetLocalState()),
            ["relativePosition"] = Convert.ToBase64String(RelativePosition.FromTypeIndex(doc.GetText("t"), 4).Encode()),
            ["relativePositionIndex"] = 4L,
        };
        File.WriteAllText(Path.Combine(InteropTests.FixtureDir, "dotnet-protocol-fixtures.json"), JsonValues.Stringify(result));
    }
}
