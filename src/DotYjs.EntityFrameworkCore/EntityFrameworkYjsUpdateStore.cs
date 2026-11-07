using DotYjs.AspNetCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DotYjs.EntityFrameworkCore;

/// <summary>
/// <see cref="IYjsUpdateStore"/> over any <see cref="DbContext"/> whose model includes <see cref="YjsUpdateEntity"/>
/// (see <see cref="YjsModelBuilderExtensions.AddYjs"/>). A new scope and context are used for every operation,
/// so the store itself is a singleton.
/// </summary>
public sealed class EntityFrameworkYjsUpdateStore<TContext> : IYjsUpdateStore where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopes;

    /// <summary>Creates the store; every operation runs in its own service scope with its own DbContext.</summary>
    public EntityFrameworkYjsUpdateStore(IServiceScopeFactory scopes) => _scopes = scopes;

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredUpdate>> LoadAsync(string docName, CancellationToken cancellationToken)
    {
        var scope = _scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<TContext>();
            return await db.Set<YjsUpdateEntity>().AsNoTracking()
                .Where(u => u.DocName == docName)
                .OrderBy(u => u.Id)
                .Select(u => new StoredUpdate(u.Id, u.Update))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<long> AppendAsync(string docName, byte[] update, CancellationToken cancellationToken)
    {
        var scope = _scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<TContext>();
            var row = new YjsUpdateEntity { DocName = docName, Update = update, CreatedUtc = DateTime.UtcNow };
            db.Set<YjsUpdateEntity>().Add(row);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return row.Id;
        }
    }

    /// <inheritdoc />
    public async Task<long> CompactAsync(string docName, byte[] state, IReadOnlyCollection<long> replacedIds, CancellationToken cancellationToken)
    {
        var scope = _scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<TContext>();
            var strategy = db.Database.CreateExecutionStrategy();
            var ids = replacedIds.ToArray();
            return await strategy.ExecuteAsync(async ct =>
            {
                var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                await using (tx.ConfigureAwait(false))
                {
                    if (ids.Length > 0)
                    {
                        await db.Set<YjsUpdateEntity>().Where(u => u.DocName == docName && ids.Contains(u.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
                    }
                    var row = new YjsUpdateEntity { DocName = docName, Update = state, CreatedUtc = DateTime.UtcNow };
                    db.Set<YjsUpdateEntity>().Add(row);
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    await tx.CommitAsync(ct).ConfigureAwait(false);
                    return row.Id;
                }
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Registers the Entity Framework Core document persistence in the service collection.</summary>
public static class YjsEntityFrameworkServiceCollectionExtensions
{
    /// <summary>
    /// Persists Yjs documents in the database behind <typeparamref name="TContext"/> (which must be registered, e.g.
    /// with <c>AddDbContext</c>, and include <see cref="YjsUpdateEntity"/> in its model via <see cref="YjsModelBuilderExtensions.AddYjs"/>).
    /// </summary>
    public static IServiceCollection AddYjsEntityFrameworkPersistence<TContext>(this IServiceCollection services, Action<UpdateLogPersistenceOptions>? configure = null)
        where TContext : DbContext
        => services.AddYjsUpdateLogPersistence<EntityFrameworkYjsUpdateStore<TContext>>(configure);
}
