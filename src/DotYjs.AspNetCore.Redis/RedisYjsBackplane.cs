using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace DotYjs.AspNetCore.Redis;

/// <summary>Options for <see cref="RedisYjsBackplane"/>.</summary>
public sealed class RedisYjsBackplaneOptions
{
    /// <summary>StackExchange.Redis configuration string (e.g. "localhost:6379"). Ignored when an <see cref="IConnectionMultiplexer"/> is registered.</summary>
    public string? Configuration { get; set; }

    /// <summary>Prefix of the pub/sub channel names; the document name is appended.</summary>
    public string ChannelPrefix { get; set; } = "dotyjs:";
}

/// <summary><see cref="IYjsBackplane"/> over Redis pub/sub: one channel per document.</summary>
#pragma warning disable VSTHRD011 // the Lazy<Task<>> is only ever awaited
public sealed class RedisYjsBackplane : IYjsBackplane, IAsyncDisposable
{
    private readonly Lazy<Task<IConnectionMultiplexer>> _connection;
    private readonly string _prefix;
    private readonly bool _ownsConnection;

    public RedisYjsBackplane(IConnectionMultiplexer connection, string channelPrefix = "dotyjs:")
    {
        _connection = new Lazy<Task<IConnectionMultiplexer>>(() => Task.FromResult(connection));
        _prefix = channelPrefix;
    }

    public RedisYjsBackplane(RedisYjsBackplaneOptions options)
    {
        if (string.IsNullOrEmpty(options.Configuration)) throw new ArgumentException("Redis configuration string is required", nameof(options));
        _connection = new Lazy<Task<IConnectionMultiplexer>>(async () => await ConnectionMultiplexer.ConnectAsync(options.Configuration!).ConfigureAwait(false));
        _prefix = options.ChannelPrefix;
        _ownsConnection = true;
    }

    private RedisChannel Channel(string docName) => RedisChannel.Literal(_prefix + docName);

    public async Task PublishAsync(string docName, YjsBackplaneMessage message, CancellationToken cancellationToken = default)
    {
        var mux = await _connection.Value.ConfigureAwait(false);
        await mux.GetSubscriber().PublishAsync(Channel(docName), message.Encode()).ConfigureAwait(false);
    }

    public async Task<IAsyncDisposable> SubscribeAsync(string docName, Func<YjsBackplaneMessage, Task> handler, CancellationToken cancellationToken = default)
    {
        var mux = await _connection.Value.ConfigureAwait(false);
        var subscriber = mux.GetSubscriber();
        var channel = Channel(docName);
        // Subscribe(channel) gives a queue that preserves message order per channel.
        var queue = await subscriber.SubscribeAsync(channel).ConfigureAwait(false);
        queue.OnMessage(async msg =>
        {
            if (msg.Message.IsNullOrEmpty) return;
            await handler(YjsBackplaneMessage.Decode((byte[])msg.Message!)).ConfigureAwait(false);
        });
        return new Subscription(queue);
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsConnection && _connection.IsValueCreated)
        {
            var mux = await _connection.Value.ConfigureAwait(false);
            await mux.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class Subscription : IAsyncDisposable
    {
        private readonly ChannelMessageQueue _queue;
        public Subscription(ChannelMessageQueue queue) { _queue = queue; }
        public async ValueTask DisposeAsync() => await _queue.UnsubscribeAsync().ConfigureAwait(false);
    }
}

public static class RedisYjsBackplaneServiceCollectionExtensions
{
    /// <summary>Registers the Redis backplane using the given StackExchange.Redis configuration string.</summary>
    public static IServiceCollection AddYjsRedisBackplane(this IServiceCollection services, string configuration, string channelPrefix = "dotyjs:")
    {
        services.TryAddSingleton<IYjsBackplane>(_ => new RedisYjsBackplane(new RedisYjsBackplaneOptions { Configuration = configuration, ChannelPrefix = channelPrefix }));
        return services;
    }

    /// <summary>Registers the Redis backplane on top of an already registered <see cref="IConnectionMultiplexer"/>.</summary>
    public static IServiceCollection AddYjsRedisBackplaneFromMultiplexer(this IServiceCollection services, string channelPrefix = "dotyjs:")
    {
        services.TryAddSingleton<IYjsBackplane>(sp => new RedisYjsBackplane(sp.GetRequiredService<IConnectionMultiplexer>(), channelPrefix));
        return services;
    }
}
