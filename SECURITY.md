# Security policy

## Reporting a vulnerability

Please do not report security issues in public issues or pull requests.

Report them privately through GitHub: open the [Security tab](https://github.com/saiganov/DotYjs/security) of the
repository and choose **Report a vulnerability**. Include the affected package and version, a description of the
issue and, if possible, a minimal reproduction (for example the bytes of an update that triggers it).

You will get an answer within a few days. Once a fix is released, the advisory is published with credit to the
reporter, unless you prefer to stay anonymous.

## Supported versions

Security fixes are made for the latest released version.

## Scope

Of particular interest:

- input from clients that crashes, hangs or exhausts the memory of a server (decoders of updates, sync and
  awareness messages); see `FuzzTests` for what is already covered
- ways to read or change a document without passing `YjsOptions.Authorize`
- leaks between documents or connections in `DotYjs.AspNetCore`

A server without an `Authorize` callback lets every client open and edit every document; that is the documented
default, not a vulnerability.

Known limitation: a crafted v2 update can describe a very large number of structs in a few bytes, so applying
v2 updates from untrusted clients can use unbounded CPU and memory (the JavaScript yjs library behaves the same).
The y-websocket protocol and the DotYjs server use v1 updates, where every struct takes at least one byte.
