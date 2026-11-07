# Contributing

Thanks for helping! Bug reports, fixes and improvements are welcome.

## Before you start

- For a bug, open an issue with a minimal reproduction. Wire-format bugs are easiest to fix with the bytes of the
  update (base64) and the JavaScript code that produced it.
- For a larger change or a new public API, open an issue first so we can agree on the approach.
- Security issues: see [SECURITY.md](SECURITY.md), not public issues.

## Build and test

You need the .NET 8 and .NET 10 SDKs and Node.js 22.

```bash
cd tests/interop && npm ci && npm run gen    # fixtures from the real JS yjs
cd ../.. && dotnet build && dotnet test      # all .NET tests, on net8.0 and net10.0
cd tests/interop && npm run check            # .NET output verified by JS yjs
```

`samples/YjsServer` and `npm run check-ws` / `npm run check-client` test the WebSocket server and client against
the real `y-websocket` (see the README).

## Guidelines

- **Compatibility first.** DotYjs follows yjs 13.6. Behaviour and bytes must match the JavaScript library; add an
  interop test (`tests/interop/gen-fixtures.mjs` plus a test in `InteropTests`) when you touch encoding or
  conflict resolution.
- **Warnings are errors.** The analyzers configured in `Directory.Build.props` and `.editorconfig` must pass.
- **Public API.** New public members must have XML documentation and be added to `PublicAPI.Unshipped.txt` of the
  package (the build tells you which lines). Avoid breaking changes; if one is necessary, explain it in the PR.
- **Tests.** Every fix comes with a test that fails without it.
- **Changelog.** Add a line to the Unreleased section of [CHANGELOG.md](CHANGELOG.md).

Pull requests are squash-merged, so the PR title becomes the commit message: keep it short and descriptive.
By contributing you agree that your contribution is licensed under the [MIT License](LICENSE).
