using DotYjs.AspNetCore;
using DotYjs.Dapper;
using DotYjs.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

// A y-websocket server that keeps documents in a database and only lets clients with a token in.
// Documents survive a restart: the update log is stored in SQLite (yjs.db) through EF Core or Dapper.
//   dotnet run --project samples/PersistentServer --urls http://localhost:5000
//   dotnet run --project samples/PersistentServer --urls http://localhost:5000 --Yjs:Persistence=Dapper
// JS client:
//   new WebsocketProvider('ws://localhost:5000/collab', 'public', doc, { params: { token: 'guest-token' } })

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Yjs") ?? "Data Source=yjs.db";

// token -> documents it may open ("*" = any); in a real app use cookies/JWT and your own permission check
var accessTokens = builder.Configuration.GetSection("Yjs:AccessTokens").Get<Dictionary<string, string[]>>() ?? [];

builder.Services.AddYjs(o =>
{
    o.Authorize = (context, docName) =>
    {
        // browsers cannot set headers on a WebSocket, so y-websocket sends extra values as query parameters
        var token = context.Request.Query["token"].ToString();
        var allowed = accessTokens.TryGetValue(token, out var docs) && (docs.Contains("*") || docs.Contains(docName));
        return ValueTask.FromResult(allowed);
    };
    // documents nobody is connected to are written to the database and dropped from memory
    o.UnloadWhenIdle = true;
});

var persistence = builder.Configuration["Yjs:Persistence"] ?? "EntityFramework";
if (string.Equals(persistence, "Dapper", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddYjsDapperPersistence(_ => new SqliteConnection(connectionString),
        configurePersistence: o => o.CompactAfterUpdates = 200);
}
else
{
    builder.Services.AddDbContext<YjsDbContext>(o => o.UseSqlite(connectionString));
    builder.Services.AddYjsEntityFrameworkPersistence<YjsDbContext>(o => o.CompactAfterUpdates = 200);
}

var app = builder.Build();

// Create the table on startup. With your own DbContext, call modelBuilder.AddYjs() and use migrations instead.
if (string.Equals(persistence, "Dapper", StringComparison.OrdinalIgnoreCase))
{
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = DapperYjsUpdateStore.CreateTableSql(DapperYjsDialect.Sqlite); // idempotent: CREATE TABLE IF NOT EXISTS
    await command.ExecuteNonQueryAsync();
}
else
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<YjsDbContext>().Database.EnsureCreatedAsync();
}

var manager = app.Services.GetRequiredService<YjsDocumentManager>();
var log = app.Services.GetRequiredService<ILogger<Program>>();
manager.DocumentLoaded += doc => log.LogInformation("Loaded {Doc} from the database", doc.Name);
manager.DocumentUnloaded += doc => log.LogInformation("Saved and unloaded {Doc}", doc.Name);

app.UseWebSockets();
app.MapYjs("/collab/{docName}");
app.MapGet("/", () => $"DotYjs server with {persistence} persistence. Connect y-websocket clients to /collab/{{doc}}?token=...");

app.Run();
