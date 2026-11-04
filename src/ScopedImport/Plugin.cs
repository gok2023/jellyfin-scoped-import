using System.Net;
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
        Version = "0.2.0.2", Libraries = library.GetVirtualFolders().Select(x => new { x.ItemId, x.Name, x.Locations, x.CollectionType }) });

    [HttpPost("PickFolder"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<IActionResult> PickFolder([FromBody] FolderPickRequest request, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult<IActionResult>(StatusCode(503, new { Message = "当前服务器无法打开 Windows 文件夹选择窗口，请手动粘贴路径。" }));
        return PickFolderCore(request, cancellationToken, WindowsFolderDialog.Show);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal async Task<IActionResult> PickFolderCore(FolderPickRequest request, CancellationToken cancellationToken,
        Func<string, CancellationToken, string?> show)
    {
        var connection = HttpContext.Connection;
        var remote = connection.RemoteIpAddress;
        if (remote?.IsIPv4MappedToIPv6 == true) remote = remote.MapToIPv4();
        var local = connection.LocalIpAddress;
        if (local?.IsIPv4MappedToIPv6 == true) local = local.MapToIPv4();
        if (remote is null || (!IPAddress.IsLoopback(remote) && !remote.Equals(local)))
            return StatusCode(403, new { Message = "请在 Jellyfin 所在电脑打开此页面，或手动粘贴路径。" });

        var folder = library.GetVirtualFolders().FirstOrDefault(entry =>
            Guid.TryParse(entry.ItemId, out var id) && id == request.LibraryId
            && entry.CollectionType?.ToString().ToLowerInvariant() is "movies" or "tvshows");
        if (folder is null || request.LocationIndex < 0 || request.LocationIndex >= folder.Locations.Length)
            return BadRequest(new { Message = "媒体库目录已变更，请刷新页面后重试。" });
        return Ok(await FolderPicker.Pick(folder.Locations[request.LocationIndex], request.PreferredPath, cancellationToken,
            show));
    }

    [HttpPost("Import")]
    public IActionResult Import([FromBody] ImportRequest request)
    {
        if (repository is not RepositoryGuard) return StatusCode(503, new { Message = "只追加保护未启用；仅支持原版 Jellyfin 10.11.11。" });
        try { return Accepted(importer.Start(request)); }
        catch (InvalidOperationException ex) { return Conflict(new { Message = ex.Message }); }
    }

    [HttpGet("Operations/{id:guid}"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Status(Guid id) => importer.Status(id) is { } state ? Ok(state) : NotFound(new OperationNotFoundResponse("ScopedImport.OperationNotFound", id,
        "记录已失效，原操作结果无法从该记录确认。"));

    [HttpGet("Audit")]
    public IActionResult Audit() => Ok(RepositoryGuard.Audit());
}

public sealed record OperationNotFoundResponse(string Code, Guid OperationId, string Message);

public sealed record FolderPickRequest(Guid LibraryId, int LocationIndex, string? PreferredPath = null);
