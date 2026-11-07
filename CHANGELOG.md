# Changelog

All notable changes are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project uses [Semantic Versioning](https://semver.org/). Until 1.0 the public API may still change between
minor versions.

## [Unreleased]

## [0.1.0-preview.1]

First public preview.

### Added

- `DotYjs`: a pure .NET port of yjs 13.6 with `Doc`, transactions and events, `YText`, `YArray`, `YMap`, the XML
  types and subdocuments; v1 and v2 updates, state vectors, `MergeUpdates`, `DiffUpdate`, format conversion and
  snapshots, all byte-compatible with the JavaScript library; `UndoManager`, `RelativePosition`,
  `PermanentUserData`; the y-protocols sync and awareness messages; `YjsWebSocketProvider`, a .NET client for any
  y-websocket server.
- `DotYjs.AspNetCore`: a WebSocket server compatible with the y-websocket client, with authorization, server-side
  document access, a persistence hook (`IYjsPersistence`) and an update-log persistence (`IYjsUpdateStore`).
- `DotYjs.AspNetCore.Redis`: a Redis backplane for several server nodes.
- `DotYjs.EntityFrameworkCore` and `DotYjs.Dapper`: ready-made document persistence.
- Fuzz tests for all decoders; corrupt input is rejected with `InvalidDataException`.

[Unreleased]: https://github.com/saiganov/DotYjs/compare/v0.1.0-preview.1...HEAD
[0.1.0-preview.1]: https://github.com/saiganov/DotYjs/releases/tag/v0.1.0-preview.1
