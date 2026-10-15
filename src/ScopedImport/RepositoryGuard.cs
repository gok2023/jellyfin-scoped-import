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
        if (typeof(BaseItem).Assembly.GetName().Version?.ToString() != "10.11.11.0"
            || descriptor.ImplementationType?.FullName != "Jellyfin.Server.Implementations.Item.BaseItemRepository") return;
        services.Remove(descriptor);
        services.AddSingleton<IItemRepository>(provider =>
        {
            var inner = (IItemRepository)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType);
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
    public IItemRepository Inner { get; set; } = null!;
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

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(method);
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
                        var conflict = Inner.RetrieveItem(item.Id) is not null;
                        if (!string.IsNullOrEmpty(item.Path))
                            conflict |= Inner.GetItemList(new InternalItemsQuery { Path = item.Path, Limit = 1 }).Count != 0;
                        if (item is Season)
                            conflict |= Inner.GetItemList(new InternalItemsQuery { ParentId = item.ParentId,
                                IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Season], IndexNumber = item.IndexNumber, Limit = 1 }).Count != 0;
                        else if (string.IsNullOrEmpty(item.Path)) throw new RepositoryProtectionException("Pathless item is unsupported");
                        if (item is Episode)
                            conflict |= Inner.GetItemList(new InternalItemsQuery { ParentId = item.ParentId,
                                IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Episode], IndexNumber = item.IndexNumber, Limit = 1 }).Count != 0;
                        if (conflict)
                        {
                            Interlocked.Increment(ref Conflicts);
                            throw new RepositoryProtectionException("AppendConflict: existing item was preserved");
                        }
                        if (Inner.RetrieveItem(item.ParentId) is null) throw new RepositoryProtectionException("Parent disappeared");
                    }
                }
                var result = Forward(method, args);
                var ids = method.Name == "SaveItems"
                    ? ((IReadOnlyList<BaseItem>)args![0]!).Select(x => x.Id).ToArray()
                    : [ (Guid)args![0]! ];
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
