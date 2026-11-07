using System.Data.Common;
using Dapper;
using DotYjs.AspNetCore;
using Microsoft.Extensions.DependencyInjection;

namespace DotYjs.Dapper;

/// <summary>Settings of <see cref="DapperYjsUpdateStore"/>.</summary>
public sealed class DapperYjsUpdateStoreOptions
{
    /// <summary>Table holding the updates; see <see cref="DapperYjsUpdateStore.CreateTableSql"/> for its layout. Default <c>yjs_updates</c>.</summary>
    public string TableName { get; set; } = "yjs_updates";
}

/// <summary>
/// <see cref="IYjsUpdateStore"/> over plain ADO.NET connections with Dapper. Uses portable SQL
/// (<c>SELECT/INSERT/DELETE</c> with named parameters; only the "last inserted id" query is provider specific and
/// picked from the connection type), so it works with SQLite, PostgreSQL, SQL Server and MySQL as long as the table
/// exists (<see cref="CreateTableSql"/>).
/// </summary>
#pragma warning disable S2077 // the only interpolated SQL fragment is the table name, validated against [A-Za-z0-9_] in the constructor; values are parameters
public sealed class DapperYjsUpdateStore : IYjsUpdateStore
{
    private readonly Func<CancellationToken, Task<DbConnection>> _connectionFactory;
    private readonly string _table;

    /// <summary>Creates a store with the default table name.</summary>
    /// <param name="connectionFactory">Returns a new, not yet opened connection. It is disposed after every operation.</param>
    public DapperYjsUpdateStore(Func<DbConnection> connectionFactory) : this(connectionFactory, null) { }

    /// <summary>Creates a store.</summary>
    /// <param name="connectionFactory">Returns a new, not yet opened connection. It is disposed after every operation.</param>
    /// <param name="options">Table name etc.; null for the defaults.</param>
    public DapperYjsUpdateStore(Func<DbConnection> connectionFactory, DapperYjsUpdateStoreOptions? options)
        : this(_ => Task.FromResult(connectionFactory()), options) { }

    /// <summary>Creates a store whose connections are created asynchronously (e.g. from a pool or a data source).</summary>
    /// <param name="connectionFactory">Returns a new, not yet opened connection. It is disposed after every operation.</param>
    /// <param name="options">Table name etc.; null for the defaults.</param>
    public DapperYjsUpdateStore(Func<CancellationToken, Task<DbConnection>> connectionFactory, DapperYjsUpdateStoreOptions? options)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _table = ValidateIdentifier((options ?? new DapperYjsUpdateStoreOptions()).TableName);
    }

    /// <summary>
    /// SQL that creates the table for the given provider. Columns: <c>id</c> (auto-increment key), <c>doc_name</c>,
    /// <c>update_data</c> (bytes), <c>created_utc</c>. Run it with your migrations tool.
    /// </summary>
    public static string CreateTableSql(DapperYjsDialect dialect, string tableName = "yjs_updates")
    {
        var t = ValidateIdentifier(tableName);
        return dialect switch
        {
            DapperYjsDialect.Sqlite =>
                $"CREATE TABLE IF NOT EXISTS {t} (id INTEGER PRIMARY KEY AUTOINCREMENT, doc_name TEXT NOT NULL, update_data BLOB NOT NULL, created_utc TEXT NOT NULL); " +
                $"CREATE INDEX IF NOT EXISTS ix_{t}_doc_name ON {t} (doc_name);",
            DapperYjsDialect.PostgreSql =>
                $"CREATE TABLE IF NOT EXISTS {t} (id BIGSERIAL PRIMARY KEY, doc_name VARCHAR(512) NOT NULL, update_data BYTEA NOT NULL, created_utc TIMESTAMP NOT NULL); " +
                $"CREATE INDEX IF NOT EXISTS ix_{t}_doc_name ON {t} (doc_name);",
            DapperYjsDialect.SqlServer =>
                $"IF OBJECT_ID('{t}') IS NULL CREATE TABLE {t} (id BIGINT IDENTITY PRIMARY KEY, doc_name NVARCHAR(512) NOT NULL, update_data VARBINARY(MAX) NOT NULL, created_utc DATETIME2 NOT NULL); " +
                $"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_{t}_doc_name') CREATE INDEX ix_{t}_doc_name ON {t} (doc_name);",
            DapperYjsDialect.MySql =>
                $"CREATE TABLE IF NOT EXISTS {t} (id BIGINT AUTO_INCREMENT PRIMARY KEY, doc_name VARCHAR(512) NOT NULL, update_data LONGBLOB NOT NULL, created_utc DATETIME NOT NULL, INDEX ix_{t}_doc_name (doc_name));",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect)),
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredUpdate>> LoadAsync(string docName, CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var rows = await connection.QueryAsync<(long Id, byte[] Update)>(new CommandDefinition(
            $"SELECT id, update_data FROM {_table} WHERE doc_name = @DocName ORDER BY id",
            new { DocName = docName }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.Select(r => new StoredUpdate(r.Id, r.Update)).ToList();
        }
    }

    /// <inheritdoc />
    public async Task<long> AppendAsync(string docName, byte[] update, CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            return await InsertAsync(connection, null, docName, update, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<long> CompactAsync(string docName, byte[] state, IReadOnlyCollection<long> replacedIds, CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (tx.ConfigureAwait(false))
            {
                // delete in chunks: some providers limit the number of parameters per statement
                foreach (var chunk in replacedIds.Chunk(500))
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                        $"DELETE FROM {_table} WHERE doc_name = @DocName AND id IN @Ids", new { DocName = docName, Ids = chunk }, tx, cancellationToken: cancellationToken)).ConfigureAwait(false);
                }
                var id = await InsertAsync(connection, tx, docName, state, cancellationToken).ConfigureAwait(false);
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                return id;
            }
        }
    }

    /// <summary>
    /// Inserts a row and returns its id. Uses the provider's "last inserted id" function, chosen by the connection type,
    /// so the SQL stays portable.
    /// </summary>
    private async Task<long> InsertAsync(DbConnection connection, DbTransaction? tx, string docName, byte[] update, CancellationToken cancellationToken)
    {
        var insert = $"INSERT INTO {_table} (doc_name, update_data, created_utc) VALUES (@DocName, @Update, @Created)";
        var args = new { DocName = docName, Update = update, Created = DateTime.UtcNow };
        var dialect = DetectDialect(connection);
        var sql = dialect switch
        {
            DapperYjsDialect.PostgreSql => insert + " RETURNING id",
            DapperYjsDialect.SqlServer => insert + "; SELECT CAST(SCOPE_IDENTITY() AS BIGINT)",
            DapperYjsDialect.MySql => insert + "; SELECT LAST_INSERT_ID()",
            _ => insert + "; SELECT last_insert_rowid()",
        };
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(sql, args, tx, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static DapperYjsDialect DetectDialect(DbConnection connection)
    {
        var name = connection.GetType().FullName ?? "";
        if (name.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)) return DapperYjsDialect.PostgreSql;
        if (name.Contains("SqlClient", StringComparison.OrdinalIgnoreCase)) return DapperYjsDialect.SqlServer;
        if (name.Contains("MySql", StringComparison.OrdinalIgnoreCase)) return DapperYjsDialect.MySql;
        return DapperYjsDialect.Sqlite;
    }

    private async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = await _connectionFactory(cancellationToken).ConfigureAwait(false);
        try
        {
            if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static string ValidateIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name) || !name.All(c => char.IsLetterOrDigit(c) || c == '_'))
            throw new ArgumentException("Table name must consist of letters, digits and underscores.", nameof(name));
        return name;
    }
}

/// <summary>The SQL dialect used by <see cref="DapperYjsUpdateStore"/> for the table definition and for reading the id of an inserted row.</summary>
public enum DapperYjsDialect
{
    /// <summary>SQLite.</summary>
    Sqlite,
    /// <summary>PostgreSQL.</summary>
    PostgreSql,
    /// <summary>Microsoft SQL Server.</summary>
    SqlServer,
    /// <summary>MySQL or MariaDB.</summary>
    MySql,
}

/// <summary>Registers the Dapper-based document persistence in the service collection.</summary>
public static class YjsDapperServiceCollectionExtensions
{
    /// <summary>Persists Yjs documents through Dapper. <paramref name="connectionFactory"/> returns a new connection per operation.</summary>
    public static IServiceCollection AddYjsDapperPersistence(this IServiceCollection services, Func<IServiceProvider, DbConnection> connectionFactory,
        Action<DapperYjsUpdateStoreOptions>? configureStore = null, Action<UpdateLogPersistenceOptions>? configurePersistence = null)
    {
        services.AddSingleton<IYjsUpdateStore>(sp =>
        {
            var options = new DapperYjsUpdateStoreOptions();
            configureStore?.Invoke(options);
            return new DapperYjsUpdateStore(() => connectionFactory(sp), options);
        });
        return services.AddYjsUpdateLogPersistence<DapperYjsUpdateStore>(configurePersistence);
    }
}
