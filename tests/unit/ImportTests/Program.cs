using System.Reflection;
using System.Text;
using System.Text.Json;
using ScopedImport;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Entities;
using Emby.Naming.Common;
using Microsoft.Extensions.Logging.Abstractions;


var fixtureRoot = Path.Combine(Environment.CurrentDirectory, "artifacts", "import-fixtures-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixtureRoot);
var count = 0;
void Assert(bool value, string message) { if (!value) throw new Exception(message); }
async Task Test(string name, Func<Task> test) { await test(); count++; Console.WriteLine("PASS: " + name); }
void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
Harness New() => new(Path.Combine(fixtureRoot, Guid.NewGuid().ToString("N")));
string Meta(JsonElement status) => status.GetProperty("Movies")[0].GetProperty("MetadataStatus").GetString()!;
await Test("operation status missing contract and successful status remain distinguishable", async () =>
{
    var h=New();var importer=(Importer)typeof(Harness).GetField("importer",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(h)!;
    var controller=new ScopedImport.Controller(importer,null!,null!);var missingId=Guid.NewGuid();
    var missing=controller.Status(missingId) as Microsoft.AspNetCore.Mvc.NotFoundObjectResult;
    Assert(missing?.StatusCode==404 && missing.Value is OperationNotFoundResponse { Code: "ScopedImport.OperationNotFound" } body && body.OperationId==missingId,"missing record contract");
    var id=h.Start(false);await h.Finish(id);
    var success=controller.Status(id) as Microsoft.AspNetCore.Mvc.OkObjectResult;
    Assert(success?.StatusCode==200 && JsonSerializer.SerializeToElement(success.Value).GetProperty("Id").GetGuid()==id,"valid record response changed");
    var method=typeof(ScopedImport.Controller).GetMethod("Status")!;
    Assert(typeof(ScopedImport.Controller).GetCustomAttribute<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()?.Policy==MediaBrowser.Common.Api.Policies.RequiresElevation
        && method.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>() is null
        && method.GetCustomAttribute<Microsoft.AspNetCore.Mvc.ResponseCacheAttribute>()?.NoStore==true,"status authorization/cache boundary");
});
foreach (var content in new string?[] { null, "<movie><title>Local Title</title><plot>local</plot><!-- <!DOCTYPE harmless --><note><![CDATA[<!DOCTYPE harmless]]></note><year>1999</year><uniqueid type=\"tmdb\">123</uniqueid></movie>", "https://example.invalid/movie/123", "", "<movie><title>broken", "<unsupported><title>wrong</title></unsupported>" })
{
    await Test("local NFO: " + (content is null ? "absent" : content.Length == 0 ? "empty" : content.StartsWith("<movie><title>Local") ? "valid" : content.StartsWith("https") ? "text" : content.StartsWith("<unsupported") ? "unsupported" : "broken"), async () =>
    {
        var h = New(); if (content is not null) File.WriteAllText(h.Nfo, content);
        var status = await h.Run(false);
        Assert(status.GetProperty("State").GetString() == "Completed", status.ToString());
        Assert(h.Created.Count == 1 && h.ProviderSelections == 0 && h.Probes == 1, "local mode provider/probe/create counts");
        Assert(Meta(status) == "LocalOnly", "local status");
        var m = h.Created.Single();
        if (content?.StartsWith("<movie><title>Local") == true) Assert(m.Name == "Local Title" && m.ProductionYear == 1999 && m.ProviderIds["Tmdb"] == "123", "local metadata not persisted");
        else Assert(m.Name == "Example Movie" && m.ProductionYear == 2001, "name/year parsing");
        if (content is not null && !content.StartsWith("<movie><title>Local")) Assert(status.GetProperty("Warnings").GetArrayLength() == 1, "one content warning");
        if (content is not null) Assert(File.ReadAllText(h.Nfo) == content, "source NFO changed");
        var again = await h.Run(true);
        Assert(h.Created.Count == 1 && h.ProviderSelections == 0 && h.Probes == 1, "duplicate import touched existing item");
        Assert(Meta(again) == "ExistingSkipped", "existing movie guidance missing");
        Assert(again.GetProperty("Movies")[0].GetProperty("ItemId").GetGuid() == m.Id, "existing link wrong movie");
    });
}
await Test("size and DTD security failures release gate; subsequent new mode succeeds", async () =>
{
    foreach (var content in new[] { new string('x', 2_000_001), "<!DOCTYPE movie [<!ENTITY x SYSTEM 'https://example.invalid/entity'>]><movie><title>&x;</title></movie>" })
    {
        var h = New(); File.WriteAllText(h.Nfo, content);
        var failed = await h.Run(false);
        Assert(failed.GetProperty("State").GetString() == "Rejected" && h.Created.Count == 0, "security error degraded");
        File.WriteAllText(h.Nfo, "ordinary text");
        h.Selected = [new Remote("ok", (info, _) => { Assert(info.Name == "Example Movie" && info.Year == 2001, "clean movie lookup input"); return Task.FromResult(new MetadataResult<Movie> { HasMetadata = true, Item = new Movie { Name = "Remote" } }); })];
        var next = await h.Run(true);
        Assert(next.GetProperty("State").GetString()=="Completed", next.ToString());
        Assert(Meta(next) == "FetchedFields" && h.Created.Single().Name == "Remote" && h.ProviderSelections == 1, "new request/mode blocked");
    }
});
await Test("DTD encodings remain rejected without resolving entities", () =>
{
    var text = " \n<!DOCTYPE movie [<!ENTITY e SYSTEM 'file:///unreadable'>]><movie>&e;</movie>";
    foreach (var encoding in new Encoding[] { Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.UTF32, new UTF32Encoding(true, true) })
    {
        foreach (var bom in new[] { true, false })
        {
            var file = Path.Combine(fixtureRoot, Guid.NewGuid() + ".nfo");
            File.WriteAllBytes(file, (bom ? encoding.GetPreamble() : []).Concat(encoding.GetBytes(text)).ToArray());
            Throws<NfoSafetyException>(() => Importer.ReadNfo(file, new(Guid.NewGuid())));
        }
    }
    return Task.CompletedTask;
});
await Test("NFO access/sharing and missing paths are fatal, not content warnings", () =>
{
    var h = New(); File.WriteAllText(h.Nfo, "<movie/>"); var op = new Operation(Guid.NewGuid());
    using (var held = new FileStream(h.Nfo, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Throws<IOException>(() => Importer.ReadNfo(h.Nfo, op));
    Throws<FileNotFoundException>(() => Importer.ReadNfo(h.Nfo + ".missing", op));
    Assert(op.Warnings.Count == 0, "access error swallowed"); return Task.CompletedTask;
});
await Test("NFO junction path remains fatal", () =>
{
    var h=New();File.WriteAllText(h.Nfo,"<movie/>");var link=Path.Combine(fixtureRoot,"junction-"+Guid.NewGuid().ToString("N"));
    var command="New-Item -ItemType Junction -Path '"+link.Replace("'","''")+"' -Target '"+h.Target.Replace("'","''")+"' | Out-Null";
    var start=new System.Diagnostics.ProcessStartInfo("pwsh"){UseShellExecute=false,CreateNoWindow=true};start.ArgumentList.Add("-NoProfile");start.ArgumentList.Add("-EncodedCommand");start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));
    using var process=System.Diagnostics.Process.Start(start)!;process.WaitForExit();
    Assert(process.ExitCode==0 && File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint),"fixture junction unavailable");
    Throws<IOException>(()=>Importer.ReadNfo(Path.Combine(link,"movie.nfo"),new(Guid.NewGuid())));
    return Task.CompletedTask;
});
await Test("shared episode NFO cache, numbering and ambiguity rejection", () =>
{
    var h = New(); File.WriteAllText(h.Nfo, "<episodedetails><season>2</season><episode>3</episode><title>Episode</title></episodedetails>");
    var op = new Operation(Guid.NewGuid()); var season = Importer.NfoNumber(h.Nfo, "season", op); var episode = Importer.NfoNumber(h.Nfo, "episode", op);
    var series = Path.Combine(fixtureRoot, "Series"); var video = Path.Combine(series, "hash.mkv");
    var placement = EpisodeLayout.Resolve(video, series, season, episode, [], new NamingOptions());
    Assert(placement.Season == 2 && placement.Episode == 3 && op.ReadFiles.Count == 1, "episode parsing/cache");
    Throws<IOException>(() => EpisodeLayout.Resolve(Path.Combine(series, "S01E03.mkv"), series, season, episode, [], new NamingOptions()));
    File.WriteAllText(h.Nfo, "broken");op = new(Guid.NewGuid());
    Assert(Importer.NfoNumber(h.Nfo, "season", op) is null, "invalid NFO number");
    Throws<IOException>(() => EpisodeLayout.Resolve(video, series, null, null, [], new NamingOptions()));
    Throws<IOException>(() => EpisodeLayout.Resolve(Path.Combine(series, "E01.mkv"), series, null, null, [1,2], new NamingOptions()));
    return Task.CompletedTask;
});
await Test("provider order, lookup name/year/ID and merge saved on new movie only", async () =>
{
    var h = New();File.WriteAllText(h.Nfo, "<movie><title>Chosen</title><year>1998</year><plot></plot><tmdbid>123</tmdbid></movie>");
    var calls = new List<string>();
    h.Selected = [new Remote("B", (info, _) => { calls.Add("B"); Assert(info.Name == "Chosen" && info.Year == 1998 && info.ProviderIds["Tmdb"] == "123" && info.MetadataLanguage == "en" && info.MetadataCountryCode == "US", "lookup inputs"); return Task.FromResult(new MetadataResult<Movie>{HasMetadata=true,Item=new Movie{Name="B",Overview="B plot",ProductionYear=1981,Genres=["Drama"],ProviderIds=new(){{"Imdb","tt123"}}}}); }),
        new Remote("A", (info, _) => { calls.Add("A"); Assert(info.ProviderIds["Imdb"] == "tt123", "IDs not forwarded"); return Task.FromResult(new MetadataResult<Movie>{HasMetadata=true,Item=new Movie{Name="A",Overview="A plot",OfficialRating="PG"}}); })];
    var status = await h.Run(true);var m = h.Created.Single();
    Assert(calls.SequenceEqual(new[]{"B","A"}) && Meta(status)=="FetchedFields", "provider ordering/result");
    Assert(m.Name=="Chosen" && m.ProductionYear==1998 && m.Overview=="B plot" && m.OfficialRating=="PG" && m.Genres.SequenceEqual(new[]{"Drama"}), "merged fields not persisted");
    Assert(h.Unrelated.Name=="untouched" && h.Store.Count==4, "unrelated changed");
});
foreach (var mode in new[]{"none","disabled","failure","timeout"})
{
    await Test("library providers: " + mode, async () =>
    {
        var h=New();File.WriteAllText(h.Nfo,"bad nfo");
        h.Selected=mode switch {
            "disabled"=>[],
            "failure"=>[new Remote("failing",(_,_)=>throw new HttpRequestException("synthetic"))],
            "timeout"=>[new Remote("timeout",async (_,ct)=>{await Task.Delay(Timeout.InfiniteTimeSpan,ct);return new();})],
            _=>[new Remote("none",(_,_)=>Task.FromResult(new MetadataResult<Movie>{HasMetadata=false}))]};
        var status=await h.Run(true);
        Assert(status.GetProperty("State").GetString()=="Completed" && h.Created.Count==1, status.ToString());
        Assert(Meta(status)==(mode=="none"?"NoMatch":mode=="disabled"?"ProvidersUnavailable":mode=="failure"?"ProviderFailed":"ProviderTimeout"),"metadata outcome classification");
        Assert(h.Created.Single().Name=="Example Movie", "fallback name lost");
    });
}
await Test("provider failure fallback does not override earlier priority", async () =>
{
    var h=New();h.Selected=[new Remote("broken",(_,_)=>throw new IOException("fixture network IO")),new Remote("ok",(_,_)=>Task.FromResult(new MetadataResult<Movie>{HasMetadata=true,Item=new Movie{Name="Fallback"}}))];
    Assert(Meta(await h.Run(true))=="FetchedFields" && h.Created.Single().Name=="Fallback","fallback failed");
});
await Test("repository writes and access violations from providers are fatal", async () =>
{
    foreach(var access in new[]{false,true})
    {
        var h=New();h.Selected=[new Remote("unsafe",(_,ct)=>{if(access)throw new UnauthorizedAccessException("fixture");h.Guarded.SaveItems([h.Unrelated],ct);return Task.FromResult(new MetadataResult<Movie>());})];
        var status=await h.Run(true);Assert(status.GetProperty("State").GetString()=="Rejected" && h.Created.Count==0 && h.Unrelated.Name=="untouched","protection swallowed");
    }
});
await Test("task cancellation escapes metadata fallback", async () =>
{
    var h=New();h.Selected=[new Remote("must-not-run",(_,_)=>throw new Exception("cancelled provider invoked"))];
    var importer=(Importer)typeof(Harness).GetField("importer",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(h)!;
    var method=typeof(Importer).GetMethod("Get",BindingFlags.NonPublic|BindingFlags.Instance)!.MakeGenericMethod(typeof(Movie),typeof(MovieInfo));
    var task=(Task<BaseItem?>)method.Invoke(importer,[new Movie(),new MovieInfo(),new LibraryOptions(),new Operation(Guid.NewGuid()),new CancellationToken(true)])!;
    try {await task;throw new Exception("task cancellation swallowed");}catch(OperationCanceledException){}
});
await Test("in-flight duplicate rejected and terminal releases task lock", async () =>
{
    var h=New();var entered=new TaskCompletionSource();var release=new TaskCompletionSource();
    h.Selected=[new Remote("hold",async (_,_)=>{entered.SetResult();await release.Task;return new();})];
    var id=h.Start(true);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Throws<InvalidOperationException>(()=>h.Start(false));release.SetResult();await h.Finish(id);
    Assert(Meta(await h.Run(false))=="ExistingSkipped" && h.Created.Count==1,"gate/duplicate handling");
});
Console.WriteLine($"PASS: {count} offline import/NFO/provider groups. Synthetic files and in-memory interfaces only; no Jellyfin host or database.");

public class Stub : DispatchProxy
{
    public Func<MethodInfo,object?[]?,object?> Handler = null!;
    protected override object? Invoke(MethodInfo? method,object?[]? args)=>Handler(method!,args);
    public static T Make<T>(Func<MethodInfo,object?[]?,object?> handler) where T:class {var p=Create<T,Stub>();((Stub)(object)p).Handler=handler;return p;}
}
public sealed class Harness
{
    public string Nfo {get;}
    public string Target {get;}
    public List<BaseItem> Created {get;}=[];
    public Dictionary<Guid,BaseItem> Store {get;}=[];
    public Movie Unrelated {get;}=new(){Id=Guid.NewGuid(),Name="untouched",Path="unrelated-sentinel"};
    public int ProviderSelections => providerStub.Selections;
    public int Probes;
    private readonly ProviderStub providerStub = new();
    public IMetadataProvider<Movie>[] Selected { get => providerStub.Selected; set => providerStub.Selected = value; }
    public IItemRepository Guarded {get;}
    private readonly Importer importer;
    public Harness(string directory)
    {
        var root=Path.Combine(directory,"movies");Target=Path.Combine(root,"Example Movie (2001)");Directory.CreateDirectory(Target);File.WriteAllBytes(Path.Combine(Target,"video.mkv"),[0]);Nfo=Path.Combine(Target,"movie.nfo");
        var physical=new Folder{Id=Guid.NewGuid(),Path=root,Name="root"};var collection=new CollectionFolder{Id=Guid.NewGuid(),PhysicalFolderIds=[physical.Id]};
        Store[physical.Id]=physical;Store[collection.Id]=collection;Store[Unrelated.Id]=Unrelated;
        var inner=Stub.Make<IItemRepository>((m,a)=>m.Name switch {
            "RetrieveItem"=>Store.GetValueOrDefault((Guid)a![0]!),
            "GetItemList"=>Query((InternalItemsQuery)a![0]!),
            "SaveItems"=>Save((IReadOnlyList<BaseItem>)a![0]!),
            _=>throw new Exception("Unexpected repository call "+m.Name)});
        Guarded=DispatchProxy.Create<IItemRepository,RepositoryGuard>();((RepositoryGuard)(object)Guarded).Inner=inner;
        var library=Stub.Make<ILibraryManager>((m,a)=>m.Name switch {
            "GetVirtualFolders"=>new List<VirtualFolderInfo>{new(){ItemId=collection.Id.ToString(),Locations=[root],CollectionType=CollectionTypeOptions.movies,LibraryOptions=new(){PreferredMetadataLanguage="en",MetadataCountryCode="US"}}},
            "FindByPath"=>Store.Values.FirstOrDefault(x=>x.Path==(string)a![0]!),
            "GetItemById"=>Store.GetValueOrDefault((Guid)a![0]!),
            "GetNewItemId"=>new Guid(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes((string)a![0]!))),
            "CreateItem"=>Create((BaseItem)a![0]!),
            _=>throw new Exception("Unexpected library call "+m.Name)});
        var providers=providerStub;
        var encoder=Stub.Make<IMediaEncoder>((m,a)=>{if(m.Name!="GetMediaInfo")throw new Exception("Unexpected encoder call "+m.Name);Probes++;return Task.FromResult(new MediaInfo{Container="matroska",MediaStreams=[]});});
        var streams=Stub.Make<IMediaStreamRepository>((m,a)=>{if(m.Name!="SaveMediaStreams")throw new Exception("Unexpected streams call "+m.Name);return null;});
        var config=Stub.Make<IServerConfigurationManager>((m,a)=>m.Name=="get_Configuration"?new ServerConfiguration():throw new Exception("Unexpected config call "+m.Name));
        BaseItem.LibraryManager=library;BaseItem.ItemRepository=Guarded;BaseItem.ConfigurationManager=config;BaseItem.Logger=NullLogger<BaseItem>.Instance;
        importer=new(library,Guarded,providers,encoder,streams,new NamingOptions(),config);
    }
    private List<BaseItem> Query(InternalItemsQuery q)=>Store.Values.Where(x=>(q.Path is null || x.Path==q.Path) && (q.ParentId==Guid.Empty || x.ParentId==q.ParentId)).ToList();
    private object? Save(IReadOnlyList<BaseItem> items){foreach(var item in items)Store.Add(item.Id,item);return null;}
    private object? Create(BaseItem item){Guarded.SaveItems([item],CancellationToken.None);Created.Add(item);return null;}
    public Guid Start(bool fetch)=>JsonSerializer.SerializeToElement(importer.Start(new(Target,FetchMetadata:fetch))).GetProperty("OperationId").GetGuid();
    public async Task<JsonElement> Finish(Guid id)
    {
        for(var n=0;n<5000;n++) {var s=JsonSerializer.SerializeToElement(importer.Status(id));if(s.GetProperty("State").GetString() is "Completed" or "Rejected" or "Partial")return s;await Task.Delay(10);}
        throw new Exception("fixture operation did not terminate");
    }
    public Task<JsonElement> Run(bool fetch)=>Finish(Start(fetch));
}
public sealed class Remote(string name,Func<MovieInfo,CancellationToken,Task<MetadataResult<Movie>>> get):IRemoteMetadataProvider<Movie,MovieInfo>
{
    public string Name=>name;
    public Task<MetadataResult<Movie>> GetMetadata(MovieInfo info,CancellationToken ct)=>get(info,ct);
    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo i,CancellationToken ct)=>throw new Exception("Search not authorized by fixture");
    public Task<HttpResponseMessage> GetImageResponse(string url,CancellationToken ct)=>throw new Exception("Remote image call forbidden");
}
