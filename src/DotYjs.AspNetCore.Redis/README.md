# DotYjs.AspNetCore.Redis

A Redis pub/sub backplane for the [DotYjs.AspNetCore](https://www.nuget.org/packages/DotYjs.AspNetCore) server,
so several server nodes behind a load balancer can serve the same document.

```csharp
builder.Services.AddYjs();
builder.Services.AddYjsRedisBackplane("localhost:6379");
```

Nodes exchange document updates and awareness through one channel per document; a node that loads a document
later requests the state it is missing from the others.

Documentation and samples: https://github.com/saiganov/DotYjs
