using System.Collections.Concurrent;

namespace DotYjs.AspNetCore;

/// <summary>
/// In-process <see cref="IYjsBackplane"/>: connects several <see cref="YjsDocumentManager"/> instances inside one
/// process (tests, or multiple hosts in one application). For real multi-node deployments use a distributed
/// implementation such as the Redis backplane.
/// </summary>
public sealed class InMemoryYjsBackplane : IYjsBackplane
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Subscription, byte>> _subscriptions = new();

    /// <inheritdoc />
    public Task PublishAsync(string docName, YjsBackplaneMessage message, CancellationToken cancellationToken = default)
    {
        if (!_subscriptions.TryGetValue(docName, out var subs)) return Task.CompletedTask;
        var tasks = subs.Keys.Select(s => s.Handler(message)).ToArray();
        return Task.WhenAll(tasks);
    }

    /// <inheritdoc />
    public Task<IAsyncDisposable> SubscribeAsync(string docName, Func<YjsBackplaneMessage, Task> handler, CancellationToken cancellationToken = default)
    {
        var subs = _subscriptions.GetOrAdd(docName, _ => new ConcurrentDictionary<Subscription, byte>());
        Subscription? sub = null;
        sub = new Subscription(handler, () =>
        {
            subs.TryRemove(sub!, out _);
            if (subs.IsEmpty) _subscriptions.TryRemove(new KeyValuePair<string, ConcurrentDictionary<Subscription, byte>>(docName, subs));
        });
        subs[sub] = 0;
        return Task.FromResult<IAsyncDisposable>(sub);
    }

    private sealed class Subscription : IAsyncDisposable
    {
        private readonly Action _onDispose;

        public Subscription(Func<YjsBackplaneMessage, Task> handler, Action onDispose)
        {
            Handler = handler;
            _onDispose = onDispose;
        }

        public Func<YjsBackplaneMessage, Task> Handler { get; }

        public ValueTask DisposeAsync()
        {
            _onDispose();
            return ValueTask.CompletedTask;
        }
    }
}
