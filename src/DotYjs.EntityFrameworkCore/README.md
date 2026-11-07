# DotYjs.EntityFrameworkCore

Document persistence for the [DotYjs.AspNetCore](https://www.nuget.org/packages/DotYjs.AspNetCore) server with
Entity Framework Core, for any database provider.

```csharp
builder.Services.AddDbContext<YjsDbContext>(o => o.UseNpgsql(connectionString));
builder.Services.AddYjsEntityFrameworkPersistence<YjsDbContext>(o => o.CompactAfterUpdates = 500);
```

To use your own `DbContext`, call `modelBuilder.AddYjs()` in `OnModelCreating`. Every update is appended as a row;
the log is compacted into a single row with the full state periodically and when the document is unloaded.
Compaction only replaces rows this node has read or written, so several nodes can share one table.

Documentation and samples: https://github.com/saiganov/DotYjs
