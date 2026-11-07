# DotYjs

[![CI](https://github.com/saiganov/DotYjs/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/saiganov/DotYjs/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/vpre/DotYjs?label=NuGet)](https://www.nuget.org/packages/DotYjs)
[![codecov](https://codecov.io/gh/saiganov/DotYjs/graph/badge.svg)](https://codecov.io/gh/saiganov/DotYjs)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A pure .NET port of [Yjs](https://github.com/yjs/yjs) 13.6 (a CRDT for collaborative editing) that is
binary-compatible with JavaScript `yjs` clients. No native dependencies; use it from ASP.NET Core or any
other .NET application (net8.0 / net10.0).

## Features

- `Doc`, transactions, `Update` / `UpdateV2` events, `Observe` / `ObserveDeep`
- Types: `YText` (with formatting, delta, embeds), `YArray`, `YMap`, `YXmlFragment`, `YXmlElement`, `YXmlText`, `YXmlHook`, nested types and subdocuments
- **v1 and v2** update encoding, state vectors, `MergeUpdates`, `DiffUpdate`, `ConvertUpdateFormatV1ToV2/V2ToV1`, `Snapshot`
- Deferred application of updates with missing dependencies (pending structs / pending delete set), GC
- lib0-compatible `Encoder` / `Decoder` (varint, any-encoding, RLE codecs)
- `RelativePosition` / `AbsolutePosition` (cursor positions that survive edits) and `UndoManager`
- `y-protocols`: `SyncProtocol` (step1 / step2 / update) and `Awareness` (`DotYjs.Protocols`)
- **`DotYjs.AspNetCore`**: a WebSocket server that the regular JS `y-websocket` client connects to
  (rooms, update and awareness broadcasting, authorization, persistence hook)
- **`DotYjs.AspNetCore.Redis`**: a Redis pub/sub backplane, so several server nodes can serve the same document
- **`DotYjs.EntityFrameworkCore`** and **`DotYjs.Dapper`**: ready-made document persistence for any database
  (append-only update log with compaction, plus a shared `IYjsUpdateStore` for your own adapters)
- `YjsWebSocketProvider`: a .NET `y-websocket` client that connects from .NET to any y-websocket server
  (JS or DotYjs), with reconnect and backoff, sync and awareness
- `PermanentUserData`: maps client IDs and deletions to users inside the document

## Example

```csharp
using DotYjs;

var doc = new Doc();
var text = doc.GetText("content");
doc.Update += (update, origin, _, _) => Broadcast(update); // any JS yjs client understands these bytes

text.Insert(0, "Hello");
text.Format(0, 5, new Dictionary<string, object?> { ["bold"] = true });

// Apply an update received from a JS client (Y.encodeStateAsUpdate / the 'update' event)
UpdateCodec.ApplyUpdate(doc, bytesFromClient);

// Full state for a new client (or a delta against its state vector)
byte[] full = UpdateCodec.EncodeStateAsUpdate(doc);
byte[] diff = UpdateCodec.EncodeStateAsUpdate(doc, clientStateVector);
```

## ASP.NET Core server (compatible with `y-websocket`)

```csharp
using DotYjs.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddYjs(o =>
{
    // o.Authorize = (ctx, docName) => ValueTask.FromResult(ctx.User.Identity?.IsAuthenticated == true);
});
// builder.Services.AddSingleton<IYjsPersistence, MyDbPersistence>(); // your own document loading/saving

var app = builder.Build();
app.UseWebSockets();
app.MapYjs("/collab/{docName}");
app.Run();
```

A JS client connects as it would to any `y-websocket` server:

```js
import * as Y from 'yjs'
import { WebsocketProvider } from 'y-websocket'

const doc = new Y.Doc()
const provider = new WebsocketProvider('ws://localhost:5000/collab', 'my-room', doc)
provider.awareness.setLocalStateField('user', { name: 'alice' })
```

Server code can read and modify documents through `YjsDocumentManager`
(changes are broadcast to all connected clients automatically):

```csharp
app.MapPost("/api/docs/{name}/append", async (string name, string text, YjsDocumentManager docs) =>
{
    var doc = await docs.GetOrCreateAsync(name);
    doc.Transact(d => d.GetText("t").Insert((int)d.GetText("t").Length, text));
});
```

### Persistence

`IYjsPersistence` is the counterpart of `persistence = { bindState, writeState }` in y-websocket:
`BindStateAsync` is called when a document is loaded into memory, and `WriteStateAsync` when it is unloaded
(the last client left). Without it, documents live in memory only. The ready-made implementations keep an
append-only update log (like `y-leveldb`): every update is appended as a row, and the log is periodically,
and on unload, compacted into a single row holding the full state. Compaction only replaces rows that this
node has read or written itself, so several server nodes (with a backplane) can share one table.

```csharp
// EF Core: your own DbContext (call modelBuilder.AddYjs() in OnModelCreating) or the ready-made YjsDbContext
builder.Services.AddDbContext<YjsDbContext>(o => o.UseNpgsql(connectionString));
builder.Services.AddYjsEntityFrameworkPersistence<YjsDbContext>(o => o.CompactAfterUpdates = 500);

// Dapper: any ADO.NET database; create the table with DapperYjsUpdateStore.CreateTableSql(DapperYjsDialect.PostgreSql)
builder.Services.AddYjsDapperPersistence(_ => new NpgsqlConnection(connectionString));

// Your own storage: implement the three methods of IYjsUpdateStore (LoadAsync / AppendAsync / CompactAsync)
builder.Services.AddYjsUpdateLogPersistence<MyUpdateStore>();
```

### Multiple server nodes

To serve one document from several instances (behind a load balancer), register a backplane:

```csharp
builder.Services.AddYjsRedisBackplane("localhost:6379");   // package DotYjs.AspNetCore.Redis
// or your own IYjsBackplane; InMemoryYjsBackplane is available for a single process
```

Nodes exchange document updates and awareness through one pub/sub channel per document;
a node that loads a document later requests the missing state from the others.

Sample application: [`samples/YjsServer`](samples/YjsServer). Undo/redo and cursor positions:

```csharp
var um = new UndoManager(text);          // tracks transactions with origin == null (or from TrackedOrigins)
text.Insert(0, "abc");
um.Undo(); um.Redo();

var rel = RelativePosition.FromTypeIndex(text, 2);   // Y.decodeRelativePosition understands these bytes
var abs = rel.ToAbsolutePosition(doc)?.Index;
```

## .NET client

```csharp
using DotYjs.Protocols;

var doc = new Doc();
await using var provider = new YjsWebSocketProvider("ws://localhost:1234", "my-room", doc);
await provider.WhenSynced;
provider.Awareness.SetLocalStateField("user", new Dictionary<string, object?> { ["name"] = "dotnet" });
lock (doc) doc.GetText("t").Insert(0, "hello"); // sent to the server and to other clients
provider.StatusChanged += s => Console.WriteLine(s); // Connecting / Connected / Disconnected
```

The provider applies incoming messages under `lock (doc)`, so make your own document edits under the same
lock. `samples/YjsClient` is a console sample; `tests/interop/check-dotnet-client.mjs` runs it against the
real JS `y-websocket` server.

## Compatibility tests

`tests/interop/gen-fixtures.mjs` runs scenarios in the real JS `yjs@13.6` and saves the updates;
`InteropTests` in .NET decodes them, compares the content and re-encodes them **byte for byte**, and also
replays the same scenarios in .NET and compares the resulting bytes with JS.
The reverse check (`check-dotnet-updates.mjs`) feeds updates produced by .NET to the JS library.
The same checks cover the sync/awareness messages of `y-protocols` and `RelativePosition`.
`FuzzTests` mutate valid messages (bit flips, truncation, huge varints and so on) and feed them to every decoder:
corrupt input must be rejected with `InvalidDataException`, without hanging or reserving large amounts of memory
(`DOTYJS_FUZZ_ITERATIONS=5000 dotnet test --filter FuzzTests` for a longer run).
`check-websocket-server.mjs` connects two real `y-websocket` clients to a running
`samples/YjsServer` and verifies sync, awareness and server-side edits.

```bash
cd tests/interop && npm install && npm run gen
cd ../.. && dotnet test
cd tests/interop && npm run check
dotnet run --project samples/YjsServer --urls http://localhost:5055 &   # in a separate terminal
cd tests/interop && npm run check-ws
cd tests/interop && npm run check-client   # .NET client against the JS y-websocket server
```

## Benchmarks

[`benchmarks/DotYjs.Benchmarks`](benchmarks/DotYjs.Benchmarks) measures the hot paths with
[BenchmarkDotNet](https://benchmarkdotnet.org), including allocations (`MemoryDiagnoser`):

- `TextEditingBenchmarks`: typing into a `YText` (append, prepend, random positions, deletes, formatting), one transaction per edit
- `CollectionBenchmarks`: `YArray` push and insert, `YMap` set
- `EncodingBenchmarks`: encoding a document edited by two clients as a v1/v2 update, loading it, applying incremental
  updates, `MergeUpdates`, `DiffUpdate` and v1 to v2 conversion
- `SyncProtocolBenchmarks`: the sync step 1 / step 2 handshake of a new client
- `Lib0Benchmarks`: the lib0 varint, string and any encoding

```bash
dotnet run -c Release --project benchmarks/DotYjs.Benchmarks -- --filter '*'             # everything
dotnet run -c Release --project benchmarks/DotYjs.Benchmarks -- --filter '*Encoding*'    # one class
dotnet run -c Release --project benchmarks/DotYjs.Benchmarks -- --filter '*' --job short # quicker, less precise
```

Results are written to `BenchmarkDotNet.Artifacts/results`. CI runs every benchmark once (`--job dry`) to check
that they still work; those numbers are not meaningful.

## Code quality

- Analyzers are enabled in [`Directory.Build.props`](Directory.Build.props) and configured in [`.editorconfig`](.editorconfig):
  .NET analyzers (`latest-recommended`) and IDE style rules, `Meziantou.Analyzer`, `Roslynator.Analyzers`,
  `SonarAnalyzer.CSharp`, `IDisposableAnalyzers`, `Microsoft.VisualStudio.Threading.Analyzers`,
  `Microsoft.CodeAnalysis.PublicApiAnalyzers`. Warnings fail the build (`TreatWarningsAsErrors`),
  and so do known vulnerabilities in dependencies (`NuGetAudit`).
- The public API of each package is recorded in `PublicAPI.Shipped.txt`; new API must first be added to
  `PublicAPI.Unshipped.txt`, otherwise the build fails with `RS0016`. This makes every contract change visible in the diff.
- CI measures coverage (`coverlet`, settings in [`tests/coverlet.runsettings`](tests/coverlet.runsettings)) and
  publishes a ReportGenerator report to the job summary, as an artifact and as a PR comment.
- A separate CI job runs [Semgrep](https://semgrep.dev) with the `p/csharp`, `p/secrets`, `p/github-actions` and
  `p/javascript` rulesets; findings fail the job, and the SARIF file is attached as an artifact.

```bash
dotnet test --collect:"XPlat Code Coverage" --settings tests/coverlet.runsettings   # coverage locally
```

## Publishing

The `DotYjs`, `DotYjs.AspNetCore`, `DotYjs.AspNetCore.Redis`, `DotYjs.EntityFrameworkCore` and `DotYjs.Dapper`
packages are published by the [`release.yml`](.github/workflows/release.yml) workflow when a GitHub Release is
published. The release tag must look like `v1.2.3` (or `v1.2.3-preview.1`) and point to a commit on `main`;
the package version comes from the tag. The workflow builds and tests the code, pushes the packages to NuGet.org
and attaches them to the release.

NuGet.org authentication uses [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing),
so no API key is stored in the repository. It needs a Trusted Publishing policy on nuget.org (repository owner
`saiganov`, repository `DotYjs`, workflow file `release.yml`) and the repository variable `NUGET_USER` with the
nuget.org profile name that owns the policy. License: [MIT](LICENSE).

Package validation (`EnablePackageValidation`) runs on every `dotnet pack`. After the first release, set
`PackageValidationBaselineVersion` in [`Directory.Build.props`](Directory.Build.props) to the released version so that
later packages are also checked for breaking changes against it. Release notes live in [CHANGELOG.md](CHANGELOG.md).

## Contributing and security

See [CONTRIBUTING.md](CONTRIBUTING.md) for how to build and test. Please report vulnerabilities privately as described
in [SECURITY.md](SECURITY.md), not in public issues.

## Roadmap

- [x] `RelativePosition`, `UndoManager`, `PermanentUserData`
- [x] y-protocols: sync (step1/step2/update) and awareness
- [x] ASP.NET Core: `app.MapYjs(...)`, rooms, persistence
- [x] Scaling out to several server nodes (backplane, Redis)
- [x] NuGet packages (release workflow on tags)
- [x] .NET `y-websocket` client (connect from .NET to any y-websocket server)
- [x] Ready-made `IYjsPersistence` implementations (EF Core, Dapper)
