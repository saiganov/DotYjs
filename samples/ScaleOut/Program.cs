using DotYjs.AspNetCore;
using DotYjs.AspNetCore.Redis;
using Microsoft.Extensions.FileProviders;

// Several server nodes serving the same documents, connected through Redis pub/sub.
// Start Redis and two nodes, then open the editor on each node: edits and presence flow between them.
//   docker run --rm -p 6379:6379 redis:7
//   dotnet run --project samples/ScaleOut --urls http://localhost:5001
//   dotnet run --project samples/ScaleOut --urls http://localhost:5002
//   open http://localhost:5001 and http://localhost:5002
// Behind a load balancer, clients of one room may land on any node; no sticky sessions are needed.

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddYjs();
builder.Services.AddYjsRedisBackplane(builder.Configuration["Redis"] ?? "localhost:6379");

var app = builder.Build();

// reuse the editor page of the CollaborativeEditor sample
var editorPage = new PhysicalFileProvider(Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "CollaborativeEditor", "wwwroot")));
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = editorPage });
app.UseStaticFiles(new StaticFileOptions { FileProvider = editorPage });
app.UseWebSockets();
app.MapYjs("/collab/{docName}");

var manager = app.Services.GetRequiredService<YjsDocumentManager>();
app.MapGet("/api/node", () => Results.Json(new { node = manager.NodeId, documents = manager.DocumentNames }));

app.Run();
