using System.Reflection;
using ScopedImport;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Persistence;

// In-memory objects only: no media fixtures, filesystem inventory, host or database.
var store = new Dictionary<Guid, BaseItem>();
var parent = new Folder { Id = Guid.NewGuid() }; store[parent.Id] = parent;
var fresh = new Movie { Id = Guid.NewGuid(), ParentId = parent.Id, Path = "C:/contract/new.mkv" };
var old = new Movie { Id = Guid.NewGuid(), ParentId = parent.Id, Path = "C:/contract/old.mkv" }; store[old.Id] = old;
var writes = 0; var images = 0; var reattachments = 0;
var query = Stub.Make<IItemRepository>((m, a) => m.Name switch {
    "RetrieveItem" => store.GetValueOrDefault((Guid)a![0]!),
    "GetItemList" => store.Values.Where(x => x.Path == ((InternalItemsQuery)a![0]!).Path).ToList(),
    _ => throw new Exception("Unexpected query: " + m.Name)
});
var writer = DispatchProxy.Create<IItemPersistenceService, RepositoryGuard>();
var guard = (RepositoryGuard)(object)writer; guard.Lookup = query;
guard.Inner = Stub.Make<IItemPersistenceService>((m, a) => {
    switch (m.Name) {
        case "SaveItems": foreach(var item in (IReadOnlyList<BaseItem>)a![0]!) store[item.Id] = item; writes++; return null;
        case "SaveImagesAsync": images++; return Task.CompletedTask;
        case "ReattachUserDataAsync": reattachments++; return Task.CompletedTask;
        case "DeleteItem": return null;
        default: throw new Exception("Unexpected write: " + m.Name);
    }
});
void Denied(Action work) { try { work(); } catch (RepositoryProtectionException) { return; } throw new Exception("Protection was bypassed"); }
using(RepositoryGuard.ReadOnlyScope()) {
    Denied(() => writer.SaveItems([fresh], default));
    Denied(() => writer.SaveImagesAsync(old));
    Denied(() => writer.ReattachUserDataAsync(old, default));
}
using(RepositoryGuard.AppendScope([fresh.Id])) {
    Denied(() => writer.SaveItems([old], default));
    Denied(() => writer.DeleteItem(old.Id));
    writer.SaveItems([fresh], default);
    Denied(() => writer.SaveItems([fresh], default));
}
using(RepositoryGuard.NewItemMetadataScope([fresh])) {
    writer.SaveItems([fresh], default);
    await writer.SaveImagesAsync(fresh);
    await writer.ReattachUserDataAsync(fresh, default);
    Denied(() => writer.SaveImagesAsync(old));
    Denied(() => writer.ReattachUserDataAsync(old, default));
    fresh.Path = "C:/contract/changed.mkv";
    Denied(() => writer.SaveItems([fresh], default));
}
if (writes != 2 || images != 1 || reattachments != 0 || !store.ContainsKey(old.Id)) throw new Exception("Unexpected forwarded writes");
writer.DeleteItem([Guid.NewGuid(),Guid.NewGuid()]);
var inventory = new ResolutionDirectory(); inventory.Invalidate("C:/contract");
if(inventory.GetFilePaths("C:/contract", true).Count != 0) throw new Exception("Inventory widened");
try { inventory.Move("C:/contract/a", "C:/contract/b"); throw new Exception("Move was permitted"); } catch(IOException) { }
Console.WriteLine("PASS: 12.4 persistence proxy preserves append-only, read-only, new-item identity, async image, user-data, delete audit and fixed inventory boundaries; no files or database accessed.");

public class Stub : DispatchProxy {
    public Func<MethodInfo, object?[]?, object?> Handler = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    public static T Make<T>(Func<MethodInfo,object?[]?,object?> handler) where T:class {
        var proxy=Create<T,Stub>(); ((Stub)(object)proxy).Handler=handler; return proxy;
    }
}
