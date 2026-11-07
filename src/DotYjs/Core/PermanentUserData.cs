using DotYjs.Lib0;

namespace DotYjs;

/// <summary>
/// Port of yjs <c>PermanentUserData</c>: stores, inside the document itself, which user owned which client ids and
/// which ranges each user deleted. Lets you attribute inserts (by client id) and deletes (by deleted id, e.g. for
/// <see cref="YText.ToDelta(Snapshot?, Snapshot?, Func{string, ID, object?}?)"/>) to users even after reconnects.
/// </summary>
public sealed class PermanentUserData
{
    private readonly Dictionary<long, string> _clients = new();
    private readonly Dictionary<string, DeleteSet> _dss = new();

    public PermanentUserData(Doc doc, YMap? storeType = null)
    {
        Doc = doc;
        Users = storeType ?? doc.GetMap("users");
        Users.Observe((e, _) =>
        {
            foreach (var key in ((YMapEvent)e).KeysChanged)
            {
                if (key != null && Users.Get(key) is YMap user) InitUser(user, key);
            }
        });
        foreach (var (key, value) in Users)
        {
            if (value is YMap user) InitUser(user, key);
        }
    }

    public Doc Doc { get; }

    /// <summary>The shared map holding the user data (<c>users</c> root type by default).</summary>
    public YMap Users { get; }

    /// <summary>Client id → user description.</summary>
    public IReadOnlyDictionary<long, string> Clients => _clients;

    /// <summary>User description → everything this user deleted.</summary>
    public IReadOnlyDictionary<string, DeleteSet> DeleteSets => _dss;

    private static DeleteSet DecodeDs(byte[] encoded) => DeleteSet.Read(new DSDecoderV1(new Decoder(encoded)));

    private static byte[] EncodeDs(DeleteSet ds)
    {
        var encoder = new DSEncoderV1();
        DeleteSet.Write(encoder, ds);
        return encoder.ToArray();
    }

    private void InitUser(YMap user, string userDescription)
    {
        var ds = (YArray)user.Get("ds")!;
        var ids = (YArray)user.Get("ids")!;
        void AddClientId(object? clientId)
        {
            if (clientId != null) _clients[Convert.ToInt64(clientId, System.Globalization.CultureInfo.InvariantCulture)] = userDescription;
        }
        ds.Observe((e, _) =>
        {
            foreach (var item in e.Changes.Added)
            {
                foreach (var encodedDs in item.Content.GetContent())
                {
                    if (encodedDs is byte[] bytes)
                    {
                        _dss[userDescription] = DeleteSet.Merge(new[] { _dss.TryGetValue(userDescription, out var cur) ? cur : new DeleteSet(), DecodeDs(bytes) });
                    }
                }
            }
        });
        // Merge with what we already know for this user: when a concurrent peer overwrote the user entry, the
        // replacement map does not carry the deletions recorded in the losing map.
        var sets = ds.ToArray().OfType<byte[]>().Select(DecodeDs).ToList();
        if (_dss.TryGetValue(userDescription, out var existing)) sets.Insert(0, existing);
        _dss[userDescription] = DeleteSet.Merge(sets);
        ids.Observe((e, _) =>
        {
            foreach (var item in e.Changes.Added)
            {
                foreach (var id in item.Content.GetContent()) AddClientId(id);
            }
        });
        foreach (var id in ids.ToArray()) AddClientId(id);
    }

    /// <summary>
    /// Registers that <paramref name="clientId"/> (usually <c>doc.ClientID</c>) belongs to <paramref name="userDescription"/>
    /// and starts recording the deletions this client makes locally.
    /// </summary>
    /// <param name="doc">The document (the one this instance was created for).</param>
    /// <param name="clientId">Client id to attribute to the user.</param>
    /// <param name="userDescription">User name or id.</param>
    /// <param name="filter">Optionally restricts which local transactions' deletions are recorded.</param>
    public void SetUserMapping(Doc doc, long clientId, string userDescription, Func<Transaction, DeleteSet, bool>? filter = null)
    {
        var users = Users;
        var user = users.Get(userDescription) as YMap;
        if (user == null)
        {
            user = new YMap();
            user.Set("ids", new YArray());
            user.Set("ds", new YArray());
            users.Set(userDescription, user);
        }
        ((YArray)user.Get("ids")!).Push(clientId);
        users.Observe((_, _) =>
        {
            var userOverwrite = users.Get(userDescription) as YMap;
            if (userOverwrite != null && !ReferenceEquals(userOverwrite, user))
            {
                // user was overwritten, port all data over to the next user object
                user = userOverwrite;
                foreach (var (cid, desc) in _clients.ToList())
                {
                    if (desc == userDescription) ((YArray)user.Get("ids")!).Push(cid);
                }
                if (_dss.TryGetValue(userDescription, out var ds))
                {
                    ((YArray)user.Get("ds")!).Push(EncodeDs(ds));
                }
            }
        });
        doc.AfterTransaction += (transaction, _) =>
        {
            var yds = (YArray)user.Get("ds")!;
            var ds = transaction.DeleteSet;
            if (transaction.Local && ds.Clients.Count > 0 && (filter == null || filter(transaction, ds)))
            {
                yds.Push(EncodeDs(ds));
            }
        };
    }

    /// <summary>Returns the user description that owns <paramref name="clientId"/>, or null.</summary>
    public string? GetUserByClientId(long clientId) => _clients.TryGetValue(clientId, out var u) ? u : null;

    /// <summary>Returns the user description that deleted <paramref name="id"/>, or null.</summary>
    public string? GetUserByDeletedId(ID id)
    {
        foreach (var (userDescription, ds) in _dss)
        {
            if (ds.IsDeleted(id)) return userDescription;
        }
        return null;
    }
}
