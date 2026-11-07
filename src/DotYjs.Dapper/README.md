# DotYjs.Dapper

Document persistence for the [DotYjs.AspNetCore](https://www.nuget.org/packages/DotYjs.AspNetCore) server with
Dapper, for any ADO.NET database (SQLite, PostgreSQL, SQL Server, MySQL).

```csharp
// once: create the table
await connection.ExecuteAsync(DapperYjsUpdateStore.CreateTableSql(DapperYjsDialect.PostgreSql));

builder.Services.AddYjsDapperPersistence(_ => new NpgsqlConnection(connectionString));
```

Every update is appended as a row; the log is compacted into a single row with the full state periodically and
when the document is unloaded. Compaction only replaces rows this node has read or written, so several nodes can
share one table.

Documentation and samples: https://github.com/saiganov/DotYjs
