using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CommunityToolkit.Datasync.Client.Offline;
using CommunityToolkit.Datasync.Client.Serialization;
using Microsoft.EntityFrameworkCore;

var dbContextOptions = new DbContextOptionsBuilder<DataContext>()
    .UseSqlite("Data Source=database.db;Foreign Keys=False")
    .UseLazyLoadingProxies()
    .Options;

// uncomment to test with JsonTypeInfoResolver
//DatasyncSerializer.JsonSerializerOptions.TypeInfoResolver = new CastleProxyResolver(
//    DatasyncSerializer.JsonSerializerOptions.TypeInfoResolver
//    ?? new DefaultJsonTypeInfoResolver()
//);

var key = Guid.CreateVersion7().ToString();
await using (var context = new DataContext(dbContextOptions))
{
    await context.Database.EnsureCreatedAsync();

    await context.SomeEntities.AddAsync(new SomeEntity
    {
        Id = key,
        Name = $"Test {DateTime.Now}",
        LocalNotes = "These notes should not be serialized into DatasyncOperationsQueue",
        RelatedEntity = new()
        {
            Id = Guid.CreateVersion7().ToString()
        }
    });

    await context.SaveChangesAsync();
}

// open new datacontext (e.g. user restarts application before sync)
await using (var context = new DataContext(dbContextOptions))
{
    var operationAfterInsert = await context.DatasyncOperationsQueue.FirstAsync(o => o.ItemId == key);
    var containsLocalNotes = operationAfterInsert.Item.Contains("LocalNotes");
    Console.WriteLine($"After insert, LocalNotes property is included: {containsLocalNotes}");
    Console.WriteLine($"Full item after insert: {operationAfterInsert.Item}");
    Console.WriteLine($"item type after insert: {operationAfterInsert.EntityType}");

    // item now is a lazy loading proxy which is derived from the base class
    var entity = await context.SomeEntities.FirstAsync(e => e.Id == key);

    Console.WriteLine($"Entity type in new DbContext: {entity.GetType()}");

    entity.Name = $"Updated name {DateTime.Now}";

    await context.SaveChangesAsync();

    // operations are 2 but should still be 1 since operations have not been pushed
    var operationsWithItemId = await context.DatasyncOperationsQueue.CountAsync(o => o.ItemId == key);
    var operationAfterEdit = await context.DatasyncOperationsQueue.FirstAsync(o => o.ItemId == key);
    var containsLocalNotesAfterEdit = operationAfterEdit.Item.Contains("LocalNotes");
    Console.WriteLine($"After edit, LocalNotes property is included: {containsLocalNotesAfterEdit}");
    Console.WriteLine($"Full item after edit: {operationAfterEdit.Item}");
    Console.WriteLine($"item type after edit (should still be correct type): {operationAfterEdit.EntityType}");
    Console.WriteLine($"Operations with item id after edit (should be 1): {operationsWithItemId}");
}

public class DataContext : OfflineDbContext
{
    public DataContext(DbContextOptions options)
        : base(options)
    {
    }

    protected override void OnDatasyncInitialization(DatasyncOfflineOptionsBuilder optionsBuilder)
    {
        optionsBuilder.Entity(typeof(SomeEntity), cfg => { });
        optionsBuilder.Entity(typeof(RelatedEntity), cfg => { });
    }

    public virtual DbSet<SomeEntity> SomeEntities { get; set; }

    public virtual DbSet<RelatedEntity> RelatedEntities { get; set; }
}

public abstract class DatasyncBase
{
    [Key]
    [StringLength(200)]
    public string Id { get; set; } = null!;

    public DateTimeOffset? UpdatedAt { get; set; }

    public string? Version { get; set; }

    public bool Deleted { get; set; }
}

public class SomeEntity : DatasyncBase
{
    [StringLength(255)]
    public string? Name { get; set; }

    // this should not be synchronized
    [JsonIgnore]
    [StringLength(255)]
    public string? LocalNotes { get; set; }

    [StringLength(200)]
    public string? RelatedEntityId { get; set; }

    // this property should also not be serialized
    [JsonIgnore]
    public virtual RelatedEntity? RelatedEntity { get; set; }
}

public class RelatedEntity : DatasyncBase;

// simple JsonTypeInfoResolver to solve serialization issues (does not solve 
public class CastleProxyResolver : IJsonTypeInfoResolver
{
    private const BindingFlags PropertyBindingFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const string CastleProxiesNamespace = "Castle.Proxies";
    private readonly IJsonTypeInfoResolver _baseResolver;

    public CastleProxyResolver(IJsonTypeInfoResolver baseResolver)
    {
        _baseResolver = baseResolver;
    }

    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        var typeInfo = _baseResolver.GetTypeInfo(type, options);

        if (typeInfo is null)
        {
            return null;
        }

        if (type.Namespace?.StartsWith(CastleProxiesNamespace) == true)
        {
            var baseTypeInfo = _baseResolver.GetTypeInfo(type, options);

            var realPropertyInfos =
                type.BaseType!.GetProperties(PropertyBindingFlags);

            var propertyInfosIntroducedByCastleCoreProxy =
                typeInfo.Properties
                    .ExceptBy(realPropertyInfos.Select(pi => pi.Name.ToUpper()), t => t.Name.ToUpper());

            var propertyInfosToRemove = typeInfo.Properties
                .Join(realPropertyInfos,
                    p => p.Name.ToUpper(),
                    pi => pi.Name.ToUpper(),
                    (p, pi) => (p, pi))
                .Where(t => t.pi.CustomAttributes.Any(ca => ca.AttributeType == typeof(JsonIgnoreAttribute)))
                .Select(t => t.p)
                .Concat(propertyInfosIntroducedByCastleCoreProxy)
                .DistinctBy(p => p.Name)
                .ToArray();

            foreach (var propertyInfo in propertyInfosToRemove)
            {
                typeInfo.Properties.Remove(propertyInfo);
            }
        }

        return typeInfo;
    }
}
