# DotYjs

A pure .NET port of [Yjs](https://github.com/yjs/yjs) 13.6, the CRDT for collaborative editing. Updates are
binary-compatible with the JavaScript `yjs` library, so .NET and browser clients can edit the same document.

```csharp
using DotYjs;

var doc = new Doc();
doc.Update += (update, origin, _, _) => Send(update);   // any JS yjs client can apply these bytes

var text = doc.GetText("content");
text.Insert(0, "Hello");
text.Format(0, 5, new Dictionary<string, object?> { ["bold"] = true });

UpdateCodec.ApplyUpdate(doc, bytesFromJsClient);
byte[] state = UpdateCodec.EncodeStateAsUpdate(doc);
```

Includes `YText`, `YArray`, `YMap`, the XML types, subdocuments, v1 and v2 updates, snapshots, `UndoManager`,
`RelativePosition`, the y-protocols sync and awareness messages and `YjsWebSocketProvider`, a .NET client for
any y-websocket server.

Related packages: `DotYjs.AspNetCore` (y-websocket compatible server), `DotYjs.AspNetCore.Redis` (multi-node
backplane), `DotYjs.EntityFrameworkCore` and `DotYjs.Dapper` (document persistence).

Documentation, samples and the changelog: https://github.com/saiganov/DotYjs
