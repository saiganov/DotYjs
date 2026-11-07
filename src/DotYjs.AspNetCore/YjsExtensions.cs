using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DotYjs.AspNetCore;

/// <summary>Registers the DotYjs WebSocket server in the service collection.</summary>
public static class YjsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the DotYjs server services (<see cref="YjsDocumentManager"/>, <see cref="YjsWebSocketHandler"/>).
    /// Register an <see cref="IYjsPersistence"/> implementation separately to load/store documents.
    /// </summary>
    public static IServiceCollection AddYjs(this IServiceCollection services, Action<YjsOptions>? configure = null)
    {
        services.AddOptions<YjsOptions>();
        if (configure != null) services.Configure(configure);
        services.TryAddSingleton<YjsDocumentManager>();
        services.TryAddSingleton<YjsWebSocketHandler>();
        return services;
    }

    /// <summary>
    /// Persists documents through an <see cref="IYjsUpdateStore"/> (registered as a singleton) using
    /// <see cref="UpdateLogPersistence"/>. Database adapters (DotYjs.EntityFrameworkCore, DotYjs.Dapper) build on this.
    /// </summary>
    public static IServiceCollection AddYjsUpdateLogPersistence<TStore>(this IServiceCollection services, Action<UpdateLogPersistenceOptions>? configure = null)
        where TStore : class, IYjsUpdateStore
    {
        services.AddOptions<UpdateLogPersistenceOptions>();
        if (configure != null) services.Configure(configure);
        services.TryAddSingleton<IYjsUpdateStore, TStore>();
        services.TryAddSingleton<IYjsPersistence>(sp => new UpdateLogPersistence(
            sp.GetRequiredService<IYjsUpdateStore>(),
            sp.GetRequiredService<IOptions<UpdateLogPersistenceOptions>>(),
            sp.GetRequiredService<ILogger<UpdateLogPersistence>>()));
        return services;
    }
}

/// <summary>Maps the DotYjs WebSocket endpoint.</summary>
public static class YjsEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the y-websocket compatible endpoint. The default pattern <c>/{docName}</c> matches how the JS
    /// <c>WebsocketProvider(serverUrl, roomName, doc)</c> client builds its URL: <c>{serverUrl}/{roomName}</c>.
    /// Call <c>app.UseWebSockets()</c> before this endpoint is reached.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddYjs();
    /// var app = builder.Build();
    /// app.UseWebSockets();
    /// app.MapYjs("/collab/{docName}"); // client: new WebsocketProvider("wss://host/collab", "my-room", doc)
    /// </code>
    /// </example>
    public static IEndpointConventionBuilder MapYjs(this IEndpointRouteBuilder endpoints, string pattern = "/{docName}")
    {
        var handler = endpoints.ServiceProvider.GetRequiredService<YjsWebSocketHandler>();
        return endpoints.Map(pattern, handler.HandleAsync).WithDisplayName("DotYjs WebSocket");
    }
}
