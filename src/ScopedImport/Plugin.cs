using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ScopedImport;

public sealed class Plugin(IApplicationPaths paths, IXmlSerializer serializer)
    : BasePlugin<BasePluginConfiguration>(paths, serializer), IHasWebPages
{
    public override string Name => "定向入库 Scoped Import";
    public override Guid Id => new("347d2c09-78a1-4697-9c3d-e23566122144");
    public override string Description => "按路径追加缺失电影和剧集，不使用媒体库扫描或祖先刷新。";
    public IEnumerable<PluginPageInfo> GetPages() => [new() { Name = "ScopedImport", DisplayName = "定向入库",
        EmbeddedResourcePath = "ScopedImport.import.html", EnableInMainMenu = true, MenuSection = "server", MenuIcon = "add_to_queue" }];
}

public sealed record ImportRequest(string Path, Guid? LibraryId = null, bool? FetchTmdb = null, bool? FetchMetadata = null)
{
    public bool ShouldFetchMetadata => FetchMetadata ?? FetchTmdb ?? true;
}

[ApiController, Route("ScopedImport"), Authorize(Policy = Policies.RequiresElevation)]
public sealed class Controller(Importer importer, ILibraryManager library, IItemRepository repository) : ControllerBase
{
    [AllowAnonymous, HttpGet("App")]
    public IActionResult App() => File(typeof(Plugin).Assembly.GetManifestResourceStream("ScopedImport.app.html")!, "text/html; charset=utf-8");

    [HttpGet("Libraries")]
    public IActionResult Libraries() => Ok(new { Ready = repository is RepositoryGuard,
        Version = "0.2.0", Libraries = library.GetVirtualFolders().Select(x => new { x.ItemId, x.Name, x.Locations, x.CollectionType }) });

    [HttpPost("Import")]
    public IActionResult Import([FromBody] ImportRequest request)
    {
        if (repository is not RepositoryGuard) return StatusCode(503, new { Message = "只追加保护未启用；仅支持原版 Jellyfin 10.11.11。" });
        try { return Accepted(importer.Start(request)); }
        catch (InvalidOperationException ex) { return Conflict(new { Message = ex.Message }); }
    }

    [HttpGet("Operations/{id:guid}")]
    public IActionResult Status(Guid id) => importer.Status(id) is { } state ? Ok(state) : NotFound();

    [HttpGet("Audit")]
    public IActionResult Audit() => Ok(RepositoryGuard.Audit());
}
