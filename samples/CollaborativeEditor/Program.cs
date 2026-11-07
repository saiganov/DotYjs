using DotYjs.AspNetCore;

// A collaborative text editor: the page in wwwroot uses the regular JavaScript yjs + y-websocket packages and
// talks to the DotYjs server. Open http://localhost:5000 in two browser windows and type in either.
//   dotnet run --project samples/CollaborativeEditor --urls http://localhost:5000

var builder = WebApplication.CreateBuilder(args);
// Without persistence a document lives in memory while someone is connected; see PersistentServer for storage.
builder.Services.AddYjs();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets();
app.MapYjs("/collab/{docName}");

// The server is a full Yjs peer: it can read and change documents, and the changes reach every open editor.
//   curl -X POST http://localhost:5000/api/notes/demo/announce -d "Lunch is ready"
app.MapPost("/api/notes/{docName}/announce", async (string docName, HttpRequest request, YjsDocumentManager docs) =>
{
    using var reader = new StreamReader(request.Body);
    var message = await reader.ReadToEndAsync();
    var doc = await docs.GetOrCreateAsync(docName);
    doc.Transact(d => d.GetText("note").Insert(0, $"[server {DateTime.Now:HH:mm}] {message}\n"));
    return Results.NoContent();
});
app.MapGet("/api/notes/{docName}", (string docName, YjsDocumentManager docs) =>
    docs.TryGet(docName) is { } doc ? Results.Text(doc.Read(d => d.GetText("note").ToString())) : Results.NotFound());

app.Run();
