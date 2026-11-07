namespace DotYjs.Tests;

public class PermanentUserDataTests
{
    [Fact]
    public void Attributes_Inserts_And_Deletes_To_Users_Across_Docs()
    {
        var ydoc1 = new Doc { ClientID = 1 };
        var ydoc2 = new Doc { ClientID = 2 };
        var pd1 = new PermanentUserData(ydoc1);
        var pd2 = new PermanentUserData(ydoc2);
        pd1.SetUserMapping(ydoc1, ydoc1.ClientID, "user a");
        pd2.SetUserMapping(ydoc2, ydoc2.ClientID, "user b");
        ydoc1.GetText().Insert(0, "xhi");
        ydoc1.GetText().Delete(0, 1);
        ydoc2.GetText().Insert(0, "hxxi");
        ydoc2.GetText().Delete(1, 2);
        UpdateCodec.ApplyUpdate(ydoc2, UpdateCodec.EncodeStateAsUpdate(ydoc1));
        UpdateCodec.ApplyUpdate(ydoc1, UpdateCodec.EncodeStateAsUpdate(ydoc2));

        Assert.Equal("user a", pd1.GetUserByClientId(1));
        Assert.Equal("user b", pd1.GetUserByClientId(2));
        Assert.Equal("user a", pd2.GetUserByClientId(1));
        Assert.Null(pd2.GetUserByClientId(42));
        // clocks 0-3 of each doc are the user map, its two arrays and the client id entry; text starts at clock 4:
        // 'x' of doc1 is (1,4); 'xx' of doc2 are (2,5),(2,6)
        Assert.Equal("user a", pd2.GetUserByDeletedId(new ID(1, 4)));
        Assert.Equal("user b", pd1.GetUserByDeletedId(new ID(2, 5)));
        Assert.Equal("user b", pd1.GetUserByDeletedId(new ID(2, 6)));
        Assert.Null(pd1.GetUserByDeletedId(new ID(2, 4)));

        // a third doc with the same user name as doc1, created after the data exists
        var ydoc3 = new Doc { ClientID = 3 };
        UpdateCodec.ApplyUpdate(ydoc3, UpdateCodec.EncodeStateAsUpdate(ydoc1));
        var pd3 = new PermanentUserData(ydoc3);
        Assert.Equal("user a", pd3.GetUserByDeletedId(new ID(1, 4)));
        pd3.SetUserMapping(ydoc3, ydoc3.ClientID, "user a");
        Assert.Equal("user a", pd3.GetUserByClientId(3));
        UpdateCodec.ApplyUpdate(ydoc1, UpdateCodec.EncodeStateAsUpdate(ydoc3));
        Assert.Equal("user a", pd1.GetUserByClientId(3));
    }

    [Fact]
    public void Concurrent_Registration_Of_Same_User_Keeps_Both_Delete_Sets()
    {
        // two offline docs register the same user and each delete something; after merging, one user map wins
        var a = new Doc { ClientID = 1 };
        var b = new Doc { ClientID = 2 };
        var pa = new PermanentUserData(a);
        var pb = new PermanentUserData(b);
        pa.SetUserMapping(a, 1, "same");
        pb.SetUserMapping(b, 2, "same");
        a.GetText().Insert(0, "aaa");
        a.GetText().Delete(0, 1); // (1,4)
        b.GetText().Insert(0, "bbb");
        b.GetText().Delete(0, 1); // (2,4)
        UpdateCodec.ApplyUpdate(a, UpdateCodec.EncodeStateAsUpdate(b));
        UpdateCodec.ApplyUpdate(b, UpdateCodec.EncodeStateAsUpdate(a));
        foreach (var pd in new[] { pa, pb })
        {
            Assert.Equal("same", pd.GetUserByDeletedId(new ID(1, 4)));
            Assert.Equal("same", pd.GetUserByDeletedId(new ID(2, 4)));
        }
    }

    [Fact]
    public void Deletes_Attributed_In_Snapshot_Delta()
    {
        var doc = new Doc(new DocOptions { Gc = false }) { ClientID = 1 }; // snapshots need deleted content to be kept
        var pd = new PermanentUserData(doc);
        pd.SetUserMapping(doc, doc.ClientID, "alice");
        var text = doc.GetText("t");
        text.Insert(0, "hello world");
        var before = Snapshot.FromDoc(doc);
        text.Delete(0, 6);
        var after = Snapshot.FromDoc(doc);
        var delta = text.ToDelta(after, before, (type, id) => new Dictionary<string, object?> { ["type"] = type, ["user"] = pd.GetUserByDeletedId(id) });
        Assert.Equal(2, delta.Count);
        Assert.Equal("hello ", delta[0].Insert);
        var ychange = (Dictionary<string, object?>)delta[0].Attributes!["ychange"]!;
        Assert.Equal("removed", ychange["type"]);
        Assert.Equal("alice", ychange["user"]);
        Assert.Equal("world", delta[1].Insert);
    }
}
