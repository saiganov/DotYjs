using Microsoft.EntityFrameworkCore;

namespace DotYjs.EntityFrameworkCore;

/// <summary>Adds the DotYjs entities to an Entity Framework Core model.</summary>
public static class YjsModelBuilderExtensions
{
    /// <summary>
    /// Adds the <see cref="YjsUpdateEntity"/> table to a model. Call it from <c>OnModelCreating</c> of the
    /// <see cref="DbContext"/> passed to <see cref="YjsEntityFrameworkServiceCollectionExtensions.AddYjsEntityFrameworkPersistence{TContext}"/>
    /// (not needed when using <see cref="YjsDbContext"/>).
    /// </summary>
    public static ModelBuilder AddYjs(this ModelBuilder modelBuilder, string tableName = "YjsUpdates")
    {
        modelBuilder.Entity<YjsUpdateEntity>(e =>
        {
            e.ToTable(tableName);
            e.HasKey(x => x.Id);
            e.Property(x => x.DocName).HasMaxLength(512).IsRequired();
            e.Property(x => x.Update).IsRequired();
            e.HasIndex(x => x.DocName);
        });
        return modelBuilder;
    }
}

/// <summary>A ready-to-use context holding only the Yjs table, for applications without their own DbContext.</summary>
public class YjsDbContext : DbContext
{
    /// <summary>Creates the context.</summary>
    public YjsDbContext(DbContextOptions<YjsDbContext> options) : base(options) { }

    /// <summary>The stored document updates.</summary>
    public DbSet<YjsUpdateEntity> YjsUpdates => Set<YjsUpdateEntity>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddYjs();
}
