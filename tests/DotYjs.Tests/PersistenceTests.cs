using DotYjs.AspNetCore;
using DotYjs.Dapper;
using DotYjs.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotYjs.Tests;

/// <summary>Update-log persistence: the generic logic against an in-memory store, and the EF Core / Dapper stores on SQLite.</summary>
public class PersistenceTests
{
    /// <summary>Records every call so the tests can check what reached the database.</summary>
    internal sealed class MemoryStore : IYjsUpdateStore
    {
        private readonly object _lock = new();
        private long _nextId;
        public Dictionary<string, List<StoredUpdate>> Rows { get; } = new();
        public int Appends, Compactions;
        public bool FailAppends, FailCompactions;

        public Task<IReadOnlyList<StoredUpdate>> LoadAsync(string docName, CancellationToken ct)
        {
            lock (_lock) return Task.FromResult<IReadOnlyList<StoredUpdate>>(Rows.TryGetValue(docName, out var l) ? l.ToList() : new List<StoredUpdate>());
        }
        public Task<long> AppendAsync(string docName, byte[] update, CancellationToken ct)
        {
            if (FailAppends) throw new IOException("database down");
            lock (_lock)
            {
                Appends++;
                return Task.FromResult(Insert(docName, update));
            }
        }
        public Task<long> CompactAsync(string docName, byte[] state, IReadOnlyCollection<long> replacedIds, CancellationToken ct)
        {
            if (FailCompactions) throw new IOException("database down");
            lock (_lock)
            {
                Compactions++;
                if (Rows.TryGetValue(docName, out var l)) l.RemoveAll(r => replacedIds.Contains(r.Id));
                return Task.FromResult(Insert(docName, state));
            }
        }
        private long Insert(string docName, byte[] update)
        {
            if (!Rows.TryGetValue(docName, out var l)) Rows[docName] = l = new List<StoredUpdate>();
            var id = ++_nextId;
            l.Add(new StoredUpdate(id, update));
            return id;
        }
        public int Count(string doc) { lock (_lock) return Rows.TryGetValue(doc, out var l) ? l.Count : 0; }
    }

    private static Doc LoadFromStore(MemoryStore store, string name)
    {
        var doc = new Doc();
        var rows = store.LoadAsync(name, default).GetAwaiter().GetResult();
        foreach (var r in rows) UpdateCodec.ApplyUpdate(doc, r.Update);
        return doc;
    }

    [Fact]
    public async Task Appends_Updates_And_Compacts_On_Unload()
    {
        var store = new MemoryStore();
        var persistence = new UpdateLogPersistence(store, new UpdateLogPersistenceOptions { CompactAfterUpdates = 0 });
        var doc = new Doc();
        await persistence.BindStateAsync("d", doc, default);
        doc.GetText("t").Insert(0, "a");
        doc.GetText("t").Insert(1, "b");
        await persistence.FlushAsync(doc);
        Assert.Equal(2, store.Count("d"));
        Assert.Equal("ab", LoadFromStore(store, "d").GetText("t").ToString());

        await persistence.WriteStateAsync("d", doc, default);
        Assert.Equal(1, store.Count("d"));
        Assert.Equal(1, store.Compactions);
        Assert.Equal("ab", LoadFromStore(store, "d").GetText("t").ToString());

        // after unload nothing is written any more
        doc.GetText("t").Insert(2, "c");
        await Task.Delay(50);
        Assert.Equal("ab", LoadFromStore(store, "d").GetText("t").ToString());
    }

    [Fact]
    public async Task Loads_Stored_Updates_And_Does_Not_Rewrite_Them()
    {
        var store = new MemoryStore();
        var persistence = new UpdateLogPersistence(store, new UpdateLogPersistenceOptions { CompactAfterUpdates = 0, CompactOnUnload = false });
        var src = new Doc();
        src.GetText("t").Insert(0, "stored");
        await store.AppendAsync("d", UpdateCodec.EncodeStateAsUpdate(src), default);

        var doc = new Doc();
        await persistence.BindStateAsync("d", doc, default);
        Assert.Equal("stored", doc.GetText("t").ToString());
        await persistence.FlushAsync(doc);
        Assert.Equal(1, store.Appends); // the loaded state is not appended again

        doc.GetText("t").Insert(6, "!");
        await persistence.WriteStateAsync("d", doc, default);
        Assert.Equal(2, store.Appends);
        Assert.Equal(0, store.Compactions);
        Assert.Equal("stored!", LoadFromStore(store, "d").GetText("t").ToString());
    }

    [Fact]
    public async Task State_Present_Before_Binding_Is_Stored()
    {
        var store = new MemoryStore();
        var persistence = new UpdateLogPersistence(store, new UpdateLogPersistenceOptions { CompactAfterUpdates = 0, CompactOnUnload = false });
        await store.AppendAsync("d", UpdateCodec.EncodeStateAsUpdate(Prefilled("old")), default);
        var doc = Prefilled("new"); // e.g. a document migrated from another system
        await persistence.BindStateAsync("d", doc, default);
        await persistence.FlushAsync(doc);
        Assert.Equal(2, store.Count("d"));
        Assert.Equal(doc.GetText("t").ToString(), LoadFromStore(store, "d").GetText("t").ToString());
    }

    private static Doc Prefilled(string text)
    {
        var d = new Doc();
        d.GetText("t").Insert(0, text);
        return d;
    }

    [Fact]
    public async Task Compacts_While_Loaded_After_Threshold_Without_Touching_The_Doc()
    {
        var store = new MemoryStore();
        var persistence = new UpdateLogPersistence(store, new UpdateLogPersistenceOptions { CompactAfterUpdates = 5 });
        var doc = new Doc();
        await persistence.BindStateAsync("d", doc, default);
        for (int i = 0; i < 12; i++) doc.GetArray("a").Push((long)i);
        await persistence.FlushAsync(doc);
        Assert.True(store.Compactions >= 2, "compacted at 5 and 10");
        Assert.True(store.Count("d") <= 5);
        var loaded = LoadFromStore(store, "d");
        Assert.Equal(12, loaded.GetArray("a").Length);
        Assert.Equal(UpdateCodec.EncodeStateVector(doc), UpdateCodec.EncodeStateVector(loaded));
    }

    [Fact]
    public async Task Failed_Append_Is_Repaired_By_Full_Rewrite_On_Unload()
    {
        var store = new MemoryStore();
        var persistence = new UpdateLogPersistence(store, new UpdateLogPersistenceOptions { CompactAfterUpdates = 0, CompactOnUnload = false });
        var doc = new Doc();
        await persistence.BindStateAsync("d", doc, default);
        doc.GetText("t").Insert(0, "ok");
        await persistence.FlushAsync(doc);
        store.FailAppends = true;
        doc.GetText("t").Insert(2, " lost?");
        await persistence.FlushAsync(doc);
        Assert.Equal("ok", LoadFromStore(store, "d").GetText("t").ToString());
        store.FailAppends = false;
        await persistence.WriteStateAsync("d", doc, default); // CompactOnUnload is off, but the failure forces a rewrite
        Assert.Equal(1, store.Compactions);
        Assert.Equal("ok lost?", LoadFromStore(store, "d").GetText("t").ToString());
    }

    [Fact]
    public async Task Compaction_Keeps_Rows_Appended_By_Another_Node()
    {
        // two nodes share the store (as with a backplane); node 1 compacts while node 2 has appended a row node 1 never saw
        var store = new MemoryStore();
        var p1 = new UpdateLogPersistence(store, new UpdateLogPersistenceOptions { CompactAfterUpdates = 3 });
        var p2 = new UpdateLogPersistence(store, new UpdateLogPersistenceOptions { CompactAfterUpdates = 0, CompactOnUnload = false });
        var d1 = new Doc { ClientID = 1 };
        var d2 = new Doc { ClientID = 2 };
        await p1.BindStateAsync("d", d1, default);
        await p2.BindStateAsync("d", d2, default);
        d2.GetText("t").Insert(0, "node2"); // appended by node 2, not (yet) delivered to node 1
        await p2.FlushAsync(d2);
        for (int i = 0; i < 4; i++) d1.GetArray("a").Push((long)i); // node 1 compacts after 3 rows
        await p1.FlushAsync(d1);
        Assert.True(store.Compactions >= 1);
        var loaded = LoadFromStore(store, "d");
        Assert.Equal("node2", loaded.GetText("t").ToString());
        Assert.Equal(4, loaded.GetArray("a").Length);

        await p1.WriteStateAsync("d", d1, default); // full-state compaction on unload: node 2's row still survives
        loaded = LoadFromStore(store, "d");
        Assert.Equal("node2", loaded.GetText("t").ToString());
        Assert.Equal(4, loaded.GetArray("a").Length);
    }

    [Fact]
    public async Task Failed_Unload_Write_Keeps_Persisting_Later_Edits()
    {
        var store = new MemoryStore();
        var persistence = new UpdateLogPersistence(store, new UpdateLogPersistenceOptions { CompactAfterUpdates = 0 });
        var doc = new Doc();
        await persistence.BindStateAsync("d", doc, default);
        doc.GetText("t").Insert(0, "a");
        await persistence.FlushAsync(doc);
        store.FailCompactions = true;
        await Assert.ThrowsAsync<IOException>(() => persistence.WriteStateAsync("d", doc, default));
        // the document manager keeps the document alive; edits made now must still reach the log
        doc.GetText("t").Insert(1, "b");
        await persistence.FlushAsync(doc);
        Assert.Equal("ab", LoadFromStore(store, "d").GetText("t").ToString());
        store.FailCompactions = false;
        await persistence.WriteStateAsync("d", doc, default);
        Assert.Equal(1, store.Count("d"));
        Assert.Equal("ab", LoadFromStore(store, "d").GetText("t").ToString());
        doc.GetText("t").Insert(2, "c"); // unloaded for good: nothing is written any more
        await Task.Delay(50);
        Assert.Equal("ab", LoadFromStore(store, "d").GetText("t").ToString());
    }

    // ---- real stores on SQLite -------------------------------------------------------------------------------------

    private static async Task StoreContract(IYjsUpdateStore store)
    {
        Assert.Empty(await store.LoadAsync("none", default));
        await store.AppendAsync("a", new byte[] { 1 }, default);
        await store.AppendAsync("a", new byte[] { 2, 2 }, default);
        await store.AppendAsync("b", new byte[] { 3 }, default);
        var a = await store.LoadAsync("a", default);
        Assert.Equal(new[] { new byte[] { 1 }, new byte[] { 2, 2 } }, a.Select(r => r.Update));
        Assert.True(a[0].Id < a[1].Id);
        var foreign = await store.AppendAsync("a", new byte[] { 7 }, default); // e.g. written by another node meanwhile
        var compacted = await store.CompactAsync("a", new byte[] { 9, 9, 9 }, a.Select(r => r.Id).ToArray(), default);
        var afterCompaction = await store.LoadAsync("a", default);
        Assert.Equal(new[] { foreign, compacted }, afterCompaction.Select(r => r.Id));
        Assert.Equal(new[] { new byte[] { 7 }, new byte[] { 9, 9, 9 } }, afterCompaction.Select(r => r.Update));
        Assert.Equal(new[] { new byte[] { 3 } }, (await store.LoadAsync("b", default)).Select(r => r.Update));
        await store.CompactAsync("new", new byte[] { 4 }, Array.Empty<long>(), default); // nothing to replace: plain insert
        Assert.Single(await store.LoadAsync("new", default));

        // through the persistence: a real document survives a reload
        var persistence = new UpdateLogPersistence(store, new UpdateLogPersistenceOptions { CompactAfterUpdates = 3 });
        var doc = new Doc();
        await persistence.BindStateAsync("doc", doc, default);
        for (int i = 0; i < 7; i++) doc.GetText("t").Insert(i, ((char)('a' + i)).ToString());
        doc.GetMap("m").Set("k", new Dictionary<string, object?> { ["n"] = 1L });
        await persistence.WriteStateAsync("doc", doc, default);
        Assert.Single(await store.LoadAsync("doc", default));
        var reloaded = new Doc();
        await persistence.BindStateAsync("doc", reloaded, default);
        Assert.Equal("abcdefg", reloaded.GetText("t").ToString());
        Assert.Equal(1L, ((Dictionary<string, object?>)reloaded.GetMap("m").Get("k")!)["n"]);
    }

    private static string TempDb() => Path.Combine(Path.GetTempPath(), "dotyjs-" + Guid.NewGuid().ToString("N") + ".db");

    [Fact]
    public async Task EntityFramework_Store_On_Sqlite()
    {
        var file = TempDb();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDbContext<YjsDbContext>(o => o.UseSqlite("Data Source=" + file));
        services.AddYjsEntityFrameworkPersistence<YjsDbContext>();
        await using var sp = services.BuildServiceProvider();
        using (var scope = sp.CreateScope()) await scope.ServiceProvider.GetRequiredService<YjsDbContext>().Database.EnsureCreatedAsync();
        Assert.IsType<UpdateLogPersistence>(sp.GetRequiredService<IYjsPersistence>());
        await StoreContract(sp.GetRequiredService<IYjsUpdateStore>());
        SqliteConnection.ClearAllPools();
        File.Delete(file);
    }

    private sealed class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder mb) => mb.AddYjs("collab_updates");
    }

    [Fact]
    public async Task EntityFramework_Store_With_Application_Context_And_Custom_Table()
    {
        var file = TempDb();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite("Data Source=" + file));
        services.AddYjsEntityFrameworkPersistence<AppDbContext>();
        await using var sp = services.BuildServiceProvider();
        using (var scope = sp.CreateScope()) await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
        await StoreContract(sp.GetRequiredService<IYjsUpdateStore>());
        await using (var c = new SqliteConnection("Data Source=" + file))
        {
            await c.OpenAsync();
            var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM collab_updates";
            Assert.True((long)(await cmd.ExecuteScalarAsync())! > 0);
        }
        SqliteConnection.ClearAllPools();
        File.Delete(file);
    }

    [Fact]
    public async Task Dapper_Store_On_Sqlite()
    {
        var file = TempDb();
        var cs = "Data Source=" + file;
        await using (var c = new SqliteConnection(cs))
        {
            await c.OpenAsync();
            var cmd = c.CreateCommand();
            cmd.CommandText = DapperYjsUpdateStore.CreateTableSql(DapperYjsDialect.Sqlite);
            await cmd.ExecuteNonQueryAsync();
        }
        var store = new DapperYjsUpdateStore(() => new SqliteConnection(cs));
        await StoreContract(store);

        // DI registration
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddYjsDapperPersistence(_ => new SqliteConnection(cs));
        await using var sp = services.BuildServiceProvider();
        Assert.IsType<DapperYjsUpdateStore>(sp.GetRequiredService<IYjsUpdateStore>());
        Assert.IsType<UpdateLogPersistence>(sp.GetRequiredService<IYjsPersistence>());
        Assert.Single(await sp.GetRequiredService<IYjsUpdateStore>().LoadAsync("doc", default));
        SqliteConnection.ClearAllPools();
        File.Delete(file);
    }

    [Fact]
    public void Dapper_Rejects_Unsafe_Table_Names()
    {
        Assert.Throws<ArgumentException>(() => new DapperYjsUpdateStore(() => new SqliteConnection(), new DapperYjsUpdateStoreOptions { TableName = "x; DROP TABLE y" }));
        foreach (var d in Enum.GetValues<DapperYjsDialect>()) Assert.Contains("doc_name", DapperYjsUpdateStore.CreateTableSql(d));
    }

    [Fact]
    public async Task Server_Round_Trip_Through_Update_Log()
    {
        // the document manager loads, edits, unloads and reloads a document through the update log
        var store = new MemoryStore();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IYjsUpdateStore>(store);
        services.AddYjsUpdateLogPersistence<MemoryStore>(o => o.CompactAfterUpdates = 0);
        services.AddYjs(o => o.UnloadWhenIdle = false);
        await using var sp = services.BuildServiceProvider();
        var manager = sp.GetRequiredService<YjsDocumentManager>();
        var persistence = (UpdateLogPersistence)sp.GetRequiredService<IYjsPersistence>();
        var d = await manager.GetOrCreateAsync("room");
        d.Transact(x => x.GetText("t").Insert(0, "hi"));
        await persistence.FlushAsync(d.Doc);
        Assert.Equal(1, store.Count("room"));
        await persistence.WriteStateAsync("room", d.Doc, default);
        Assert.Equal("hi", LoadFromStore(store, "room").GetText("t").ToString());
    }
}
