using System.Security.Cryptography;

namespace DotYjs;

/// <summary>Options for creating a <see cref="Doc"/>.</summary>
public sealed class DocOptions
{
    /// <summary>Globally unique identifier for this document. Defaults to a random UUID.</summary>
    public string? Guid { get; set; }

    /// <summary>Associates this document with a collection (only relevant for subdocuments).</summary>
    public string? CollectionId { get; set; }

    /// <summary>Disable garbage collection by setting this to false.</summary>
    public bool Gc { get; set; } = true;

    /// <summary>Called before an Item is garbage collected. Return false to keep the item.</summary>
    public Func<Item, bool>? GcFilter { get; set; }

    /// <summary>Any kind of meta information you want to associate with this document (sent with subdocuments).</summary>
    public object? Meta { get; set; }

    /// <summary>If a subdocument, automatically load document.</summary>
    public bool AutoLoad { get; set; }

    /// <summary>Whether the document should be synced by the provider now.</summary>
    public bool ShouldLoad { get; set; } = true;
}

/// <summary>Arguments of <see cref="Doc.SubdocsChanged"/>: the subdocuments that were loaded, added or removed in a transaction.</summary>
public sealed class SubdocsEventArgs
{
    /// <summary>Creates the event arguments.</summary>
    public SubdocsEventArgs(IReadOnlySet<Doc> loaded, IReadOnlySet<Doc> added, IReadOnlySet<Doc> removed)
    {
        Loaded = loaded;
        Added = added;
        Removed = removed;
    }

    /// <summary>Subdocuments that requested to be loaded.</summary>
    public IReadOnlySet<Doc> Loaded { get; }
    /// <summary>Subdocuments that were added to the document.</summary>
    public IReadOnlySet<Doc> Added { get; }
    /// <summary>Subdocuments that were removed from the document.</summary>
    public IReadOnlySet<Doc> Removed { get; }
}

/// <summary>A Yjs document: a collection of named shared types.</summary>
public sealed class Doc
{
    private readonly TaskCompletionSource<Doc> _whenLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool> _whenSynced = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Creates a document with default options and a random client id.</summary>
    public Doc() : this(new DocOptions()) { }

    /// <summary>Creates a document with the given options and a random client id.</summary>
    public Doc(DocOptions options)
    {
        Gc = options.Gc;
        GcFilter = options.GcFilter ?? (_ => true);
        ClientID = GenerateNewClientId();
        Guid = options.Guid ?? System.Guid.NewGuid().ToString();
        CollectionId = options.CollectionId;
        ShouldLoad = options.ShouldLoad;
        AutoLoad = options.AutoLoad;
        Meta = options.Meta;
    }

    /// <summary>Generates a random 32-bit client id (like yjs).</summary>
    public static long GenerateNewClientId() => (long)RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue) & 0xffffffffL;

    /// <summary>Whether deleted content is garbage collected. Disable it to keep deleted content, for example for snapshots.</summary>
    public bool Gc { get; set; }
    /// <summary>Called before an item is garbage collected. Return false to keep the item.</summary>
    public Func<Item, bool> GcFilter { get; set; }
    /// <summary>The id of this client. It is part of the id of every element this client creates, so two clients must never share it.</summary>
    public long ClientID { get; set; }
    /// <summary>Globally unique identifier of this document.</summary>
    public string Guid { get; internal set; }
    /// <summary>The collection this document belongs to (only relevant for subdocuments).</summary>
    public string? CollectionId { get; internal set; }
    /// <summary>Meta information associated with this document (sent with subdocuments).</summary>
    public object? Meta { get; }
    /// <summary>Whether a subdocument is loaded automatically.</summary>
    public bool AutoLoad { get; }
    /// <summary>Whether the document should be loaded and synced by providers.</summary>
    public bool ShouldLoad { get; internal set; }
    /// <summary>Whether the document has been loaded (see <see cref="Load"/> and <see cref="Loaded"/>).</summary>
    public bool IsLoaded { get; private set; }
    /// <summary>Whether a provider reported that the document is synced.</summary>
    public bool IsSynced { get; private set; }
    /// <summary>Whether <see cref="Destroy"/> has been called.</summary>
    public bool IsDestroyed { get; private set; }

    /// <summary>The root types of this document by name.</summary>
    public Dictionary<string, AbstractType> Share { get; } = new();

    /// <summary>All structs of this document, grouped by client.</summary>
    public StructStore Store { get; } = new();

    internal Transaction? CurrentTransaction { get; set; }
    internal List<Transaction> TransactionCleanups { get; set; } = new();

    /// <summary>The subdocuments of this document.</summary>
    public HashSet<Doc> Subdocs { get; } = new();

    /// <summary>If this document is a subdocument, a reference to the item containing it.</summary>
    public Item? Item { get; internal set; }

    /// <summary>Completes when the document is loaded.</summary>
    public Task<Doc> WhenLoaded => _whenLoaded.Task;
    /// <summary>Completes when a provider reports that the document is synced.</summary>
    public Task WhenSynced => _whenSynced.Task;

    // ---- events ----
    /// <summary>Raised before the first transaction of a batch of nested transactions starts.</summary>
    public event Action<Doc>? BeforeAllTransactions;
    /// <summary>Raised when a transaction starts.</summary>
    public event Action<Transaction, Doc>? BeforeTransaction;
    /// <summary>Raised after a transaction, before the observers of the changed types are called.</summary>
    public event Action<Transaction, Doc>? BeforeObserverCalls;
    /// <summary>Raised after a transaction, after the observers were called.</summary>
    public event Action<Transaction, Doc>? AfterTransaction;
    /// <summary>Raised after a transaction has been cleaned up (garbage collection, merged structs).</summary>
    public event Action<Transaction, Doc>? AfterTransactionCleanup;
    /// <summary>Raised after all transactions of a batch have been cleaned up.</summary>
    public event Action<Doc, IReadOnlyList<Transaction>>? AfterAllTransactions;

    /// <summary>Emitted with a v1-encoded update after every transaction that changed the document.</summary>
    public event Action<byte[], object?, Doc, Transaction>? Update;

    /// <summary>Emitted with a v2-encoded update after every transaction that changed the document.</summary>
    public event Action<byte[], object?, Doc, Transaction>? UpdateV2;

    /// <summary>Raised when subdocuments were added, removed or requested to be loaded.</summary>
    public event Action<SubdocsEventArgs, Doc, Transaction>? SubdocsChanged;
    /// <summary>Raised when the document is destroyed.</summary>
    public event Action<Doc>? Destroyed;
    /// <summary>Raised when the document is loaded.</summary>
    public event Action<Doc>? Loaded;

    internal bool HasUpdateListeners => Update != null;
    internal bool HasUpdateV2Listeners => UpdateV2 != null;

    internal void EmitBeforeAllTransactions() => BeforeAllTransactions?.Invoke(this);
    internal void EmitBeforeTransaction(Transaction t) => BeforeTransaction?.Invoke(t, this);
    internal void EmitBeforeObserverCalls(Transaction t) => BeforeObserverCalls?.Invoke(t, this);
    internal void EmitAfterTransaction(Transaction t) => AfterTransaction?.Invoke(t, this);
    internal void EmitAfterTransactionCleanup(Transaction t) => AfterTransactionCleanup?.Invoke(t, this);
    internal void EmitAfterAllTransactions(IReadOnlyList<Transaction> ts) => AfterAllTransactions?.Invoke(this, ts);
    internal void EmitUpdate(byte[] update, Transaction t) => Update?.Invoke(update, t.Origin, this, t);
    internal void EmitUpdateV2(byte[] update, Transaction t) => UpdateV2?.Invoke(update, t.Origin, this, t);
    internal void EmitSubdocs(SubdocsEventArgs e, Transaction t) => SubdocsChanged?.Invoke(e, this, t);

    /// <summary>Signals that a provider synced (or lost sync with) this document.</summary>
    public void SetSynced(bool isSynced = true)
    {
        if (!isSynced && IsSynced)
        {
            _whenSynced = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        IsSynced = isSynced;
        if (isSynced)
        {
            _whenSynced.TrySetResult(true);
            if (!IsLoaded) MarkLoaded();
        }
    }

    /// <summary>Marks the document as loaded (emits <see cref="Loaded"/>).</summary>
    public void MarkLoaded()
    {
        IsLoaded = true;
        Loaded?.Invoke(this);
        _whenLoaded.TrySetResult(this);
    }

    /// <summary>Notify the parent document that you request to load data into this subdocument (if it is a subdocument).</summary>
    public void Load()
    {
        var item = Item;
        if (item != null && !ShouldLoad)
        {
            Transaction.Transact(((AbstractType)item.Parent!).Doc!, t => { t.SubdocsLoaded.Add(this); }, null, true);
        }
        ShouldLoad = true;
    }

    /// <summary>The guids of all subdocuments.</summary>
    public IReadOnlySet<string> GetSubdocGuids() => Subdocs.Select(d => d.Guid).ToHashSet();

    /// <summary>
    /// Changes that happen inside of a transaction are bundled. This means that the observer fires
    /// after the transaction is finished and that all changes that happened inside of the transaction are sent
    /// as one message to the other peers.
    /// </summary>
    public void Transact(Action<Transaction> f, object? origin = null) => Transaction.Transact(this, f, origin);

    /// <summary>Runs <paramref name="f"/> in a transaction (see <see cref="Transact(Action{Transaction}, object?)"/>) and returns its result.</summary>
    public T Transact<T>(Func<Transaction, T> f, object? origin = null) => Transaction.Transact(this, f, origin);

    /// <summary>Returns the root type with the given name, creating it if necessary (untyped).</summary>
    public AbstractType Get(string name)
    {
        if (!Share.TryGetValue(name, out var type))
        {
            type = new AbstractType();
            type.Integrate(this, null);
            Share[name] = type;
        }
        return type;
    }

    /// <summary>Define a shared data type. Multiple calls with the same name return the same instance.</summary>
    public T Get<T>(string name) where T : AbstractType, new()
    {
        if (!Share.TryGetValue(name, out var type))
        {
            var t = new T();
            t.Integrate(this, null);
            Share[name] = t;
            return t;
        }
        if (type is T typed && type.GetType() == typeof(T)) return typed;
        if (type.GetType() == typeof(AbstractType))
        {
            // remove the untyped placeholder and replace it with a typed instance
            var t = new T();
            t.Map = type.Map;
            foreach (var start in type.Map.Values)
            {
                for (Item? n = start; n != null; n = n.Left) n.Parent = t;
            }
            t.Start = type.Start;
            for (var n = t.Start; n != null; n = n.Right) n.Parent = t;
            t.LengthInternal = type.LengthInternal;
            if (t is YText yt)
            {
                // Formatting markers integrated into the placeholder bypassed ContentFormat.Integrate's
                // bookkeeping: restore it, otherwise search markers would skip format items.
                for (var n = t.Start; n != null; n = n.Right)
                {
                    if (n.Content is ContentFormat)
                    {
                        yt.HasFormatting = true;
                        yt.SearchMarker = null;
                        break;
                    }
                }
            }
            Share[name] = t;
            t.Integrate(this, null);
            return t;
        }
        throw new InvalidOperationException($"Type with the name {name} has already been defined with a different constructor");
    }

    /// <summary>Returns the root <see cref="YArray"/> with the given name, creating it if necessary.</summary>
    public YArray GetArray(string name = "") => Get<YArray>(name);
    /// <summary>Returns the root <see cref="YText"/> with the given name, creating it if necessary.</summary>
    public YText GetText(string name = "") => Get<YText>(name);
    /// <summary>Returns the root <see cref="YMap"/> with the given name, creating it if necessary.</summary>
    public YMap GetMap(string name = "") => Get<YMap>(name);
    /// <summary>Returns the root <see cref="YXmlElement"/> with the given name, creating it if necessary.</summary>
    public YXmlElement GetXmlElement(string name = "") => Get<YXmlElement>(name);
    /// <summary>Returns the root <see cref="YXmlFragment"/> with the given name, creating it if necessary.</summary>
    public YXmlFragment GetXmlFragment(string name = "") => Get<YXmlFragment>(name);

    /// <summary>Converts the entire document into a JSON-like value (Dictionary of root names to values).</summary>
    public Dictionary<string, object?> ToJSON()
    {
        var doc = new Dictionary<string, object?>();
        foreach (var (key, value) in Share) doc[key] = value.ToJSON();
        return doc;
    }

    /// <summary>Emit destroy event and unregister all event handlers.</summary>
    public void Destroy()
    {
        IsDestroyed = true;
        foreach (var subdoc in Subdocs.ToList()) subdoc.Destroy();
        var item = Item;
        if (item != null)
        {
            Item = null;
            var content = (ContentDoc)item.Content;
            var opts = new DocOptions { Guid = Guid, ShouldLoad = false };
            if (content.Opts.TryGetValue("gc", out var gc) && gc is bool g) opts.Gc = g;
            if (content.Opts.TryGetValue("autoLoad", out var al) && al is bool a) opts.AutoLoad = a;
            if (content.Opts.TryGetValue("meta", out var meta)) opts.Meta = meta;
            content.Doc = new Doc(opts) { Item = item };
            Transaction.Transact(((AbstractType)item.Parent!).Doc!, t =>
            {
                var doc = content.Doc;
                if (!item.Deleted) t.SubdocsAdded.Add(doc);
                t.SubdocsRemoved.Add(this);
            }, null, true);
        }
        Destroyed?.Invoke(this);
        BeforeAllTransactions = null;
        BeforeTransaction = null;
        BeforeObserverCalls = null;
        AfterTransaction = null;
        AfterTransactionCleanup = null;
        AfterAllTransactions = null;
        Update = null;
        UpdateV2 = null;
        SubdocsChanged = null;
        Destroyed = null;
        Loaded = null;
    }

    /// <inheritdoc />
    public override string ToString() => $"Doc({Guid})";
}
