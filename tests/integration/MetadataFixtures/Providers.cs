using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;
namespace MetadataFixtures;
public sealed class Plugin(IApplicationPaths p, IXmlSerializer s) : BasePlugin<BasePluginConfiguration>(p,s)
{ public override string Name => "LAB ONLY metadata fixtures"; public override Guid Id => new("67457c32-e0da-4d83-b91d-aec5291f3c8f"); }
public abstract class Fixture : IRemoteMetadataProvider<Movie,MovieInfo>
{
    public abstract string Name {get;}
    public virtual Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken ct)
    {
        EnsureLab(info);
        var result=new Movie {Name=Name, ProductionYear=Name=="Lab-A"?1981:1982, Overview=Name=="Lab-B"?"fallback overview":null};
        result.ProviderIds[Name]="present";
        if(info.ProviderIds.ContainsKey("Lab-A"))result.ProviderIds["Lab-Forwarded"]="yes";
        return Task.FromResult(new MetadataResult<Movie>{HasMetadata=true,Item=result});
    }
    protected static void EnsureLab(MovieInfo info)
    {
        var lab=Environment.GetEnvironmentVariable("JELLYFIN_TARGETED_LAB");
        if(string.IsNullOrEmpty(lab)||!Path.GetFullPath(info.Path).StartsWith(Path.GetFullPath(Path.Combine(lab,"media"))+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Lab fixture only");
    }
    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo i,CancellationToken t)=>Task.FromResult<IEnumerable<RemoteSearchResult>>([]);
    public Task<HttpResponseMessage> GetImageResponse(string url,CancellationToken t)=>throw new NotSupportedException();
}
public sealed class A : Fixture {public override string Name=>"Lab-A";}
public sealed class B : Fixture {public override string Name=>"Lab-B";}
public sealed class Failing : Fixture {public override string Name=>"Lab-Failure";public override Task<MetadataResult<Movie>> GetMetadata(MovieInfo i,CancellationToken c){EnsureLab(i);throw new IOException("Controlled provider failure");}}
public sealed class Writer(IItemRepository repository) : Fixture
{
    public override string Name=>"Lab-BlockedWriter";
    public override Task<MetadataResult<Movie>> GetMetadata(MovieInfo i,CancellationToken c)
    {
        EnsureLab(i);
        // The production importer's metadata guard must reject this BEFORE SaveItems.
        repository.SaveItems([new Movie {Id=Guid.NewGuid(),Name="forbidden provider write",Path=i.Path}],c);
        throw new Exception("Metadata write was NOT blocked");
    }
}
