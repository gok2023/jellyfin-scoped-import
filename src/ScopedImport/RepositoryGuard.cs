using System.Reflection;
using System.Runtime.ExceptionServices;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace ScopedImport;

public sealed class Services : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        services.AddSingleton<Importer>();
        var descriptor = services.Last(x => x.ServiceType == typeof(IItemRepository));
        // Fixed ABI: never replace an unknown repository or compose unknown decorators.
        if (typeof(BaseItem).Assembly.GetName().Version != typeof(Services).Assembly.GetReferencedAssemblies()
                .Single(a => a.Name == "MediaBrowser.Controller").Version) return;
#if JELLYFIN12
        // 12.4 shares one concrete repository between query interfaces via a factory.
        var concrete = services.LastOrDefault(x => x.ServiceType.FullName == "Jellyfin.Server.Implementations.Item.BaseItemRepository");
        if (descriptor.ImplementationFactory is null || descriptor.Lifetime != ServiceLifetime.Singleton
            || concrete?.ImplementationType != concrete?.ServiceType || concrete is null
            || concrete.Lifetime != ServiceLifetime.Singleton) return;
#else
        if (descriptor.ImplementationType?.FullName != "Jellyfin.Server.Implementations.Item.BaseItemRepository") return;
#endif
#if JELLYFIN12
        var writer = services.Last(x => x.ServiceType == typeof(IItemPersistenceService));
        if (writer.ImplementationType?.FullName != "Jellyfin.Server.Implementations.Item.ItemPersistenceService") return;
        services.Remove(writer);
        services.AddSingleton<IItemPersistenceService>(provider =>
        {
            var proxy = DispatchProxy.Create<IItemPersistenceService, RepositoryGuard>();
            var guard = (RepositoryGuard)(object)proxy;
            var inner = (IItemPersistenceService)ActivatorUtilities.CreateInstance(provider, writer.ImplementationType);
            guard.Lookup = provider.GetRequiredService<IItemRepository>();
            if (guard.Lookup is not RepositoryGuard) return inner;
            guard.Inner = inner;
            return proxy;
        });
#endif
        services.Remove(descriptor);
        services.AddSingleton<IItemRepository>(provider =>
        {
#if JELLYFIN12
            var inner = (IItemRepository)descriptor.ImplementationFactory!(provider);
            if (inner.GetType() != concrete.ServiceType) return inner; // never compose an unknown decorator
#else
            var inner = (IItemRepository)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
#endif
            var proxy = DispatchProxy.Create<IItemRepository, RepositoryGuard>();
            ((RepositoryGuard)(object)proxy).Inner = inner;
            return proxy;
        });
    }
}

// All core SaveItems calls resolve the same singleton interface. Serialize its
// ordinary writers with our check+insert. No SQL, EF, file DB access, or deletion.
// Concrete repositories instantiated by an unrelated plugin bypass this contract.
public class RepositoryGuard : DispatchProxy
{
    public object Inner { get; set; } = null!;
    public IItemRepository? Lookup { get; set; }
    private IItemRepository Repository => Inner as IItemRepository ?? Lookup ?? throw new RepositoryProtectionException("Missing query repository");
    public static readonly object SaveGate = new();
    private static readonly AsyncLocal<HashSet<Guid>?> Permitted = new();
    private static readonly AsyncLocal<bool> ReadOnly = new();
    public static IDisposable ReadOnlyScope() { if (ReadOnly.Value) throw new RepositoryProtectionException("Nested metadata scope"); ReadOnly.Value = true; return new ReadScope(); }
    private sealed class ReadScope : IDisposable { public void Dispose() => ReadOnly.Value = false; }
    public static long Conflicts;
    private static long sequence;
    private static readonly Queue<object> AuditEntries = new();
    public static object Audit() { lock (SaveGate) return new { Sequence = sequence, Entries = AuditEntries.ToArray() }; }

    public static IDisposable AppendScope(IEnumerable<Guid> ids)
    {
        if (Permitted.Value is not null) throw new RepositoryProtectionException("Nested import scope");
        Permitted.Value = ids.ToHashSet();
        return new Scope();
    }
    private sealed class Scope : IDisposable { public void Dispose() => Permitted.Value = null; }

    private sealed record Identity(Type Type, string? Path, Guid ParentId, Guid OwnerId, Guid SeriesId, Guid SeasonId, int? Number, int? ParentNumber)
    {
        public static Identity Of(BaseItem item) => new(item.GetType(), item.Path, item.ParentId, item.OwnerId,
            item is Season season ? season.SeriesId : (item as Episode)?.SeriesId ?? Guid.Empty,
            (item as Episode)?.SeasonId ?? Guid.Empty, item.IndexNumber, item.ParentIndexNumber);
    }
    private static readonly AsyncLocal<Dictionary<Guid, Identity>?> Updating = new();
    internal static IDisposable NewItemMetadataScope(IEnumerable<BaseItem> items)
    {
        if (Updating.Value is not null || Permitted.Value is not null || ReadOnly.Value)
            throw new RepositoryProtectionException("Nested metadata update scope");
        Updating.Value = items.ToDictionary(item => item.Id, Identity.Of);
        return new UpdateScope();
    }
    private sealed class UpdateScope : IDisposable { public void Dispose() => Updating.Value = null; }

    internal static void CheckSiblingEpisodes(IItemRepository repository, Episode episode)
    {
        if (episode.SeriesId == Guid.Empty || episode.ParentIndexNumber is not int seasonNumber) return;
        var seasons = repository.GetItemList(new InternalItemsQuery
        {
            ParentId = episode.SeriesId, IndexNumber = seasonNumber,
            IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Season],
            GroupByPresentationUniqueKey = false
        });
        foreach (var sibling in seasons.Where(season => season.Id != episode.ParentId))
        {
            if (sibling is not Season season || season.ParentId != episode.SeriesId || season.SeriesId != episode.SeriesId)
                throw new RepositoryProtectionException("同编号季父级或类型异常，无法确认剧集归属：" + sibling.Id);
            var episodes = repository.GetItemList(new InternalItemsQuery
            {
                ParentId = sibling.Id, IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Episode],
                GroupByPresentationUniqueKey = false
            });
            foreach (var other in episodes)
            {
                if (other is not Episode existing || existing.SeriesId != episode.SeriesId || existing.SeasonId != sibling.Id
                    || existing.ParentId != sibling.Id || existing.ParentIndexNumber != seasonNumber
                    || !existing.IndexNumber.HasValue || !episode.IndexNumber.HasValue
                    || (existing.IndexNumber <= (episode.IndexNumberEnd ?? episode.IndexNumber)
                        && episode.IndexNumber <= (existing.IndexNumberEnd ?? existing.IndexNumber))
                    || string.Equals(existing.Path, episode.Path, StringComparison.OrdinalIgnoreCase))
                    throw new RepositoryProtectionException($"同编号其它季存在剧集归属冲突：Season={sibling.Id}，Episode={other.Id}，编号={other.IndexNumber}，路径={other.Path}");
            }
        }
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (Updating.Value is { } updates)
        {
            if (method.Name is "SaveItems" or "SaveImages" or "SaveImagesAsync")
            {
                lock (SaveGate)
                {
                    IReadOnlyList<BaseItem> targets = method.Name is "SaveImages" or "SaveImagesAsync"
                        ? [(BaseItem)args![0]!] : (IReadOnlyList<BaseItem>)args![0]!;
                    foreach (var item in targets)
                    {
                        if (!updates.TryGetValue(item.Id, out var identity) || identity != Identity.Of(item)
                            || Repository.RetrieveItem(item.Id) is not { } stored || Identity.Of(stored) != identity)
                            throw new RepositoryProtectionException($"{method.Name}: metadata update outside confirmed new-item identity; ItemId={item.Id}");
                    }
                    var result = Forward(method, args);
                    AuditEntries.Enqueue(new { Sequence = ++sequence, Method = method.Name, NewItemMetadata = true,
                        Ids = targets.Select(item => item.Id).ToArray(), Utc = DateTime.UtcNow });
                    while (AuditEntries.Count > 2000) AuditEntries.Dequeue();
                    return result;
                }
            }
            if (method.Name == "ReattachUserDataAsync" && args![0] is BaseItem target
                && updates.TryGetValue(target.Id, out var expected) && expected == Identity.Of(target))
            {
                // Image completion does not reassign historical user-data rows to a newly created ID.
                return Task.CompletedTask;
            }
            if (!(method.Name.StartsWith("Get", StringComparison.Ordinal) || method.Name.StartsWith("Retrieve", StringComparison.Ordinal)
                || method.Name == "ItemExistsAsync"))
                throw new RepositoryProtectionException("Metadata update forbids repository operation: " + method.Name);
        }
        if (ReadOnly.Value && !(method.Name.StartsWith("Get", StringComparison.Ordinal) || method.Name.StartsWith("Retrieve", StringComparison.Ordinal) || method.Name == "ItemExistsAsync"))
            throw new RepositoryProtectionException("Metadata provider may not write media records: " + method.Name);
        if (method.Name is "SaveItems" or "DeleteItem")
        {
            lock (SaveGate)
            {
                var allowed = Permitted.Value;
                if (allowed is not null)
                {
                    if (method.Name != "SaveItems") throw new RepositoryProtectionException("Append scope forbids deletion");
                    foreach (var item in (IReadOnlyList<BaseItem>)args![0]!)
                    {
                        if (!allowed.Contains(item.Id)) throw new RepositoryProtectionException("Write outside new-item set");
                        var conflict = Repository.RetrieveItem(item.Id) is not null;
                        if (!string.IsNullOrEmpty(item.Path))
                            conflict |= Repository.GetItemList(new InternalItemsQuery { Path = item.Path, Limit = 1 }).Count != 0;
                        if (item is Season && item.IndexNumber.HasValue)
                            conflict |= Repository.GetItemList(new InternalItemsQuery { ParentId = item.ParentId,
                                IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Season], IndexNumber = item.IndexNumber, Limit = 1 }).Count != 0;
                        if (string.IsNullOrEmpty(item.Path) && item is not Season { IndexNumber: not null }) throw new RepositoryProtectionException("Pathless item is unsupported");
                        if (item is Episode && item.IndexNumber.HasValue)
                            conflict |= Repository.GetItemList(new InternalItemsQuery { ParentId = item.ParentId,
                                IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Episode], IndexNumber = item.IndexNumber, Limit = 1 }).Count != 0;
                        if (conflict)
                        {
                            Interlocked.Increment(ref Conflicts);
                            throw new RepositoryProtectionException("AppendConflict: existing item was preserved");
                        }
                        if (item is Episode newEpisode) CheckSiblingEpisodes(Repository, newEpisode);
                        if (Repository.RetrieveItem(item.ParentId) is null) throw new RepositoryProtectionException("Parent disappeared");
                    }
                }
                var result = Forward(method, args);
                var ids = method.Name == "SaveItems"
                    ? ((IReadOnlyList<BaseItem>)args![0]!).Select(x => x.Id).ToArray()
                    : ((IReadOnlyList<Guid>)args![0]!).ToArray();
                AuditEntries.Enqueue(new { Sequence = ++sequence, Method = method.Name,
                    AppendOnly = allowed is not null, Ids = ids, Utc = DateTime.UtcNow });
                while (AuditEntries.Count > 2000) AuditEntries.Dequeue();
                return result;
            }
        }
        if (Permitted.Value is not null && method.Name is not ("RetrieveItem" or "GetItemList" or "GetItems" or "GetItemIdsList" or "GetCount" or "ItemExistsAsync"))
            throw new RepositoryProtectionException("Unexpected repository operation in append scope: " + method.Name);
        return Forward(method, args);
    }

    private object? Forward(MethodInfo method, object?[]? args)
    {
        try { return method.Invoke(Inner, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
}

internal sealed class RepositoryProtectionException(string message) : InvalidOperationException(message);
