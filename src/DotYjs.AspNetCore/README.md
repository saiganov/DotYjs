# DotYjs.AspNetCore

An ASP.NET Core WebSocket server for [DotYjs](https://www.nuget.org/packages/DotYjs). The regular JavaScript
`y-websocket` client connects to it like to the Node.js server: documents are synced and awareness (cursors,
user names) is broadcast to everyone in the room.

```csharp
builder.Services.AddYjs(o =>
{
    o.Authorize = (ctx, docName) => ValueTask.FromResult(ctx.User.Identity?.IsAuthenticated == true);
});

var app = builder.Build();
app.UseWebSockets();
app.MapYjs("/collab/{docName}");
```

```js
new WebsocketProvider('wss://example.com/collab', 'my-room', doc)
```

Without `Authorize`, any client can open and edit any document. Server code can read and change documents through
`YjsDocumentManager`; the changes reach all connected clients. Implement `IYjsPersistence` (or use
`DotYjs.EntityFrameworkCore` / `DotYjs.Dapper`) to store documents, and `IYjsBackplane` (or use
`DotYjs.AspNetCore.Redis`) to run several server nodes.

Documentation and samples: https://github.com/saiganov/DotYjs
