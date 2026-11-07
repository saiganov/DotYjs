using DotYjs.AspNetCore;

// Minimal y-websocket compatible server. Connect from JS with:
//   new WebsocketProvider('ws://localhost:5000/collab', 'my-room', ydoc)

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddYjs(o =>
{
    o.KeepAliveInterval = TimeSpan.FromSeconds(30);
    // o.Authorize = (ctx, docName) => ValueTask.FromResult(ctx.User.Identity?.IsAuthenticated == true);
});
// builder.Services.AddSingleton<IYjsPersistence, MyDatabasePersistence>();

var app = builder.Build();
app.UseWebSockets();
app.MapYjs("/collab/{docName}");

// Read-only REST view of a document: handy for debugging and used by the interop test.
app.MapGet("/api/docs/{docName}/text/{textName}", (string docName, string textName, YjsDocumentManager docs) =>
{
    var doc = docs.TryGet(docName);
    return doc == null ? Results.NotFound() : Results.Text(doc.Read(d => d.GetText(textName).ToString()));
});
app.MapGet("/api/docs/{docName}/json", (string docName, YjsDocumentManager docs) =>
{
    var doc = docs.TryGet(docName);
    return doc == null ? Results.NotFound() : Results.Text(doc.Read(d => DotYjs.Lib0.JsonValues.Stringify(d.ToJSON())), "application/json");
});
app.MapGet("/api/docs/{docName}/awareness", (string docName, YjsDocumentManager docs) =>
{
    var doc = docs.TryGet(docName);
    if (doc == null) return Results.NotFound();
    var states = doc.Awareness.GetStates().ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
    return Results.Text(DotYjs.Lib0.JsonValues.Stringify(states), "application/json");
});
// Server-side edit: changes are broadcast to all connected clients.
app.MapPost("/api/docs/{docName}/text/{textName}", async (string docName, string textName, HttpRequest request, YjsDocumentManager docs) =>
{
    var text = await new StreamReader(request.Body).ReadToEndAsync();
    var doc = await docs.GetOrCreateAsync(docName);
    doc.Transact(d =>
    {
        var t = d.GetText(textName);
        t.Insert((int)t.Length, text);
    });
    return Results.Ok();
});

app.Run();
