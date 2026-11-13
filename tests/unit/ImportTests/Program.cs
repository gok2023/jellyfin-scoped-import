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


if (args is ["--repository-delete"])
{
    await TestRepositoryDelete();
    return;
}

var fixtureRoot = Path.Combine(Environment.CurrentDirectory, "artifacts", "import-fixtures-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixtureRoot);
var count = 0;
void Assert(bool value, string message) { if (!value) throw new Exception(message); }
async Task Test(string name, Func<Task> test) { await test(); count++; Console.WriteLine("PASS: " + name); }
void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
Harness New() => new(Path.Combine(fixtureRoot, Guid.NewGuid().ToString("N")));
string Meta(JsonElement status) => status.GetProperty("Movies")[0].GetProperty("MetadataStatus").GetString()!;
Task TestRepositoryDelete()
{
    Guid first = Guid.NewGuid(), second = Guid.NewGuid();
    var calls = 0;
    var guarded = DispatchProxy.Create<IItemRepository, RepositoryGuard>();
    ((RepositoryGuard)(object)guarded).Inner = Stub.Make<IItemRepository>((method, arguments) =>
    {
        Assert(method.Name == "DeleteItem" && ((IReadOnlyList<Guid>)arguments![0]!).SequenceEqual([first, second]),
            "expected both IDs forwarded in order");
        calls++;
        return null;
    });
    guarded.DeleteItem([first, second]);
    var entry = JsonSerializer.SerializeToElement(RepositoryGuard.Audit()).GetProperty("Entries").EnumerateArray().Last();
    Assert(calls == 1 && entry.GetProperty("Method").GetString() == "DeleteItem"
        && entry.GetProperty("Ids").EnumerateArray().Select(id => id.GetGuid()).SequenceEqual([first, second]),
        "expected one delete call and both IDs in the audit");
    Console.WriteLine("PASS: repository delete forwards and audits both IDs");
    return Task.CompletedTask;
}
await TestRepositoryDelete();
await Test("native folder picker resolves configured locations, validates results and serializes STA requests", async () =>
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    var root = Path.Combine(fixtureRoot, "动漫");
    var second = Path.Combine(fixtureRoot, "第二目录");
    var movies = Path.Combine(fixtureRoot, "电影");
    var show = Path.Combine(root, "生活 大爆炸", new string('长', 140), new string('路', 140));
    Directory.CreateDirectory(show);
    Directory.CreateDirectory(second);
    Directory.CreateDirectory(movies);
    var animeId = Guid.NewGuid();
    var movieId = Guid.NewGuid();
    var library = Stub.Make<ILibraryManager>((method, _) => method.Name == "GetVirtualFolders"
        ? new List<VirtualFolderInfo>
        {
            new() { ItemId = animeId.ToString("N"), Name = "漫", Locations = [root, second], CollectionType = CollectionTypeOptions.tvshows },
            new() { ItemId = movieId.ToString("N"), Name = "电影", Locations = [movies], CollectionType = CollectionTypeOptions.movies }
        }
        : throw new InvalidOperationException(method.Name));
    var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
    context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
    var controller = new ScopedImport.Controller(null!, library, null!) { ControllerContext = new() { HttpContext = context } };
    async Task<FolderPicker.Result> Pick(FolderPickRequest request, Func<string, CancellationToken, string?> mock,
        CancellationToken token = default) =>
        (FolderPicker.Result)((Microsoft.AspNetCore.Mvc.OkObjectResult)await controller.PickFolderCore(request, token, mock)).Value!;

    Assert(await Pick(new(animeId, 0, show), (start, _) =>
    {
        Assert(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA && start == show, "valid long preferred directory on STA");
        return show;
    }) is { Status: "Selected", Path: var path } && path == show, "selected absolute path");
    Assert(await Pick(new(animeId, 1, root), (start, _) =>
        start == second ? null : throw new Exception("wrong configured location")) is { Status: "Cancelled" }, "second location and cancellation");
    Assert(await Pick(new(movieId, 0, Path.Combine(movies, "不存在")), (start, _) =>
        start == movies ? null : throw new Exception("wrong library root")) is { Status: "Cancelled" }, "invalid default fallback");
    foreach (var invalid in new[] { movies, Path.Combine(root, "..", "电影"), Path.Combine(root, "NUL"), @"\\?\C:\" })
        Assert(await Pick(new(animeId, 0), (_, _) => invalid) is { Status: "Error", Path: null }, "invalid returned path");
    Assert(await Pick(new(animeId, 0), (_, _) => throw new InvalidOperationException()) is
        { Status: "Error", Message: var message } && message!.Contains("交互桌面"), "desktop error");

    using var disconnected = new CancellationTokenSource();
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var first = Pick(new(animeId, 0), (_, token) =>
    {
        started.SetResult();
        token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
        return show;
    }, disconnected.Token);
    try
    {
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(await Pick(new(movieId, 0), (_, _) => throw new Exception("second dialog opened")) is { Status: "Busy" }, "single window");
    }
    finally { disconnected.Cancel(); }
    Assert(await first is { Status: "Cancelled" }, "disconnect discards result");
    Assert(await controller.PickFolderCore(new(Guid.NewGuid(), 0), default, (_, _) => null)
        is Microsoft.AspNetCore.Mvc.BadRequestObjectResult, "unknown library");
    context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.0.2.10");
    Assert(await controller.PickFolderCore(new(animeId, 0), default, (_, _) => null)
        is Microsoft.AspNetCore.Mvc.ObjectResult { StatusCode: 403 }, "remote request refused");
});
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
await Test("unnumbered season and clips stay scoped and idempotent", async () =>
{
    var h = new Harness(Path.Combine(fixtureRoot, Guid.NewGuid().ToString("N")), television: true);
    var clips = Path.Combine(h.Target, "剪辑");
    Directory.CreateDirectory(clips);
    Directory.CreateDirectory(Path.Combine(h.Target, "Season 01"));
    var clip = Path.Combine(clips, "百年孤独.mp4");
    File.WriteAllBytes(clip, [0]);
    var first = await h.Finish(h.Start(false, clips));
    Assert(first.GetProperty("State").GetString() == "Completed", first.ToString());
    Assert(first.GetProperty("EnumeratedDirectories").EnumerateArray().Single().GetString() == clips, "sibling enumerated");
    var season = h.Created.OfType<MediaBrowser.Controller.Entities.TV.Season>().Single();
    var episode = h.Created.OfType<MediaBrowser.Controller.Entities.TV.Episode>().Single();
    Assert(season.Path == clips && season.IndexNumber is null && season.ParentId == h.Created[0].Id, "season path/number/parent");
    Assert(episode.Path == clip && episode.IndexNumber is null && episode.ParentIndexNumber is null
        && episode.ParentId == season.Id && episode.SeriesId == h.Created[0].Id, "episode path/number/parent");
    File.WriteAllBytes(Path.Combine(clips, "OVA.mp4"), [0]);
    var again = await h.Finish(h.Start(false, clips));
    Assert(again.GetProperty("State").GetString() == "Completed" && h.Created.Count == 4, again.ToString());
    Assert(h.Unrelated.Name == "untouched", "unrelated row changed");
});

await Test("numbered seasons append and true episode conflicts preserve rows", async () =>
{
    var h = new Harness(Path.Combine(fixtureRoot, Guid.NewGuid().ToString("N")), television: true);
    var firstSeason = Path.Combine(h.Target, "Season 01");
    Directory.CreateDirectory(firstSeason);
    File.WriteAllBytes(Path.Combine(firstSeason, "Show S01E01.mkv"), [0]);
    var first = await h.Run(false);
    Assert(first.GetProperty("State").GetString() == "Completed", first.ToString());
    var secondSeason = Path.Combine(h.Target, "Season 02");
    Directory.CreateDirectory(secondSeason);
    var secondEpisode = Path.Combine(secondSeason, "Show S02E01.mkv");
    File.WriteAllBytes(secondEpisode, [0]);
    File.WriteAllText(Path.ChangeExtension(secondEpisode, ".nfo"), "<episodedetails><title>Local title</title><season>7</season><episode>9</episode></episodedetails>");
    var second = await h.Finish(h.Start(false, secondSeason));
    Assert(second.GetProperty("State").GetString() == "Completed", second.ToString());
    var added = (MediaBrowser.Controller.Entities.TV.Episode)h.Created[^1];
    Assert(added.Name == "Local title" && added.IndexNumber == 1 && added.ParentIndexNumber == 2
        && added.ParentId == h.Created[^2].Id, "numbered episode parent or NFO boundary");
    File.WriteAllBytes(Path.Combine(secondSeason, "Other S02E01.mkv"), [0]);
    var conflict = await h.Finish(h.Start(false, secondSeason));
    Assert(conflict.GetProperty("State").GetString() == "Rejected" && h.Created.Count == 5, conflict.ToString());
});

foreach (var scenario in new[] { "new", "pathless", "virtual", "different path", "orphan", "duplicate plan", "wrong series" })
{
    await Test("Season 04: " + scenario, async () =>
    {
        var h = new Harness(Path.Combine(fixtureRoot, Guid.NewGuid().ToString("N")), television: true);
        string AddSeason(int number, string name)
        {
            var path = Path.Combine(h.Target, name);
            Directory.CreateDirectory(path);
            File.WriteAllBytes(Path.Combine(path, $"Show S{number:00}E01.mkv"), [0]);
            return path;
        }
        for (var number = 1; number <= 3; number++)
        {
            var initial = await h.Finish(h.Start(false, AddSeason(number, $"Season {number:00}")));
            Assert(initial.GetProperty("State").GetString() == "Completed", initial.ToString());
        }

        var series = (MediaBrowser.Controller.Entities.TV.Series)h.Created[0];
        var directory = AddSeason(4, "Season 04");
        var placeholder = new MediaBrowser.Controller.Entities.TV.Season
        {
            Id = Guid.NewGuid(), SeriesId = series.Id, IndexNumber = 4, Name = "Preserve title",
            IsVirtualItem = scenario == "virtual", Path = scenario == "different path" ? Path.Combine(h.Target, "Season 4") : null
        };
        placeholder.SetParent(series);
        if (scenario is "pathless" or "virtual" or "different path" or "orphan" or "wrong series") h.Store.Add(placeholder.Id, placeholder);
        if (scenario == "wrong series") placeholder.SeriesId = Guid.NewGuid();
        if (scenario == "orphan") h.Store.Remove(series.Id);
        if (scenario == "duplicate plan") AddSeason(4, "Season 4");

        var status = await h.Finish(h.Start(false, scenario == "duplicate plan" ? h.Target : directory));
        var failure = scenario switch
        {
            "virtual" => "虚拟季", "different path" => "路径不同",
            "orphan" => "历史残留", "duplicate plan" => "规划冲突", "wrong series" => "SeriesId=", _ => null
        };
        if (failure is not null)
        {
            Assert(status.GetProperty("State").GetString() == "Rejected"
                && status.GetProperty("Message").GetString()!.Contains(failure) && h.Created.Count == 7, status.ToString());
            if (scenario != "duplicate plan")
            {
                var diagnostic = status.GetProperty("Warnings").EnumerateArray().Select(x => x.GetString()!)
                    .Single(x => x.StartsWith("季冲突只读诊断 "));
                using var evidence = JsonDocument.Parse(diagnostic["季冲突只读诊断 ".Length..]);
                Assert(evidence.RootElement.EnumerateArray().Any(x => x.GetProperty("Source").GetString() == "Repository.GetItemList"
                    && x.GetProperty("Id").GetGuid() == placeholder.Id
                    && x.GetProperty("SeriesId").GetGuid() == placeholder.SeriesId), "missing exact row evidence");
            }
            return;
        }

        Assert(status.GetProperty("State").GetString() == "Completed", status.ToString());
        var season = h.Store.Values.OfType<MediaBrowser.Controller.Entities.TV.Season>().Single(s => s.IndexNumber == 4);
        var episode = (MediaBrowser.Controller.Entities.TV.Episode)h.Created[^1];
        Assert(season.ParentId == series.Id && season.SeriesId == series.Id && season.Path == (scenario == "pathless" ? null : directory)
            && episode.ParentId == season.Id && episode.SeasonId == season.Id && episode.SeriesId == series.Id
            && episode.IndexNumber == 1 && episode.ParentIndexNumber == 4
            && episode.Path == Path.Combine(directory, "Show S04E01.mkv"), "season or episode identity");
        if (scenario == "pathless")
            Assert(season.Id == placeholder.Id && season.Name == "Preserve title" && h.Created.Count == 8, "placeholder was replaced");
        var fifth = await h.Finish(h.Start(false, AddSeason(5, "Season 05")));
        Assert(fifth.GetProperty("State").GetString() == "Completed", fifth.ToString());
        var fifthEpisode = (MediaBrowser.Controller.Entities.TV.Episode)h.Created[^1];
        Assert(fifthEpisode.ParentIndexNumber == 5 && fifthEpisode.SeriesId == series.Id
            && fifthEpisode.SeasonId == h.Created[^2].Id, "fifth season relationship");
        var repeat = await h.Finish(h.Start(false, directory));
        Assert(repeat.GetProperty("State").GetString() == "Completed"
            && repeat.GetProperty("Created").GetArrayLength() == 0 && h.Unrelated.Name == "untouched", repeat.ToString());
    });
}

foreach (var conflictingEpisode in new[] { false, true })
{
    await Test("exact Season 04 wins over missing Season 04(1), conflict=" + conflictingEpisode, async () =>
    {
        var h = new Harness(Path.Combine(fixtureRoot, Guid.NewGuid().ToString("N")), television: true);
        var directory = Path.Combine(h.Target, "Season 04");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "Show S04E01.mkv"), [0]);
        Assert((await h.Finish(h.Start(false, directory))).GetProperty("State").GetString() == "Completed", "initial");
        var series = h.Created.OfType<MediaBrowser.Controller.Entities.TV.Series>().Single();
        var target = h.Created.OfType<MediaBrowser.Controller.Entities.TV.Season>().Single();
        var other = new MediaBrowser.Controller.Entities.TV.Season
        {
            Id = Guid.NewGuid(), Path = Path.Combine(h.Target, "Season 04(1)"), IndexNumber = 4,
            SeriesId = series.Id, Name = "Keep other", Overview = "Keep metadata"
        };
        other.SetParent(series);
        h.Store.Add(other.Id, other);
        Assert(!Directory.Exists(other.Path), "other path should be absent");
        if (conflictingEpisode)
        {
            var existing = new MediaBrowser.Controller.Entities.TV.Episode
            {
                Id = Guid.NewGuid(), SeriesId = series.Id, SeasonId = other.Id, ParentIndexNumber = 4,
                IndexNumber = 2, Path = Path.Combine(other.Path, "Show S04E02.mkv")
            };
            existing.SetParent(other);
            h.Store.Add(existing.Id, existing);
        }
        target.SeriesPresentationUniqueKey = "stale-series-key";
        File.WriteAllBytes(Path.Combine(directory, "Show S04E02.mkv"), [0]);
        var result = await h.Finish(h.Start(false, directory));
        Assert(result.GetProperty("State").GetString() == (conflictingEpisode ? "Rejected" : "Completed"), result.ToString());
        Assert(h.Store.Values.OfType<MediaBrowser.Controller.Entities.TV.Season>().Count() == 2
            && other.Name == "Keep other" && other.Overview == "Keep metadata" && other.ParentId == series.Id, "other season changed");
        if (!conflictingEpisode)
        {
            Assert(target.SeriesPresentationUniqueKey == "stale-series-key"
                && result.GetProperty("Warnings").EnumerateArray().Any(x => x.GetString()!.Contains("季展示关联待确认")), "missing visibility diagnosis or existing season changed");
            var added = (MediaBrowser.Controller.Entities.TV.Episode)h.Created[^1];
            Assert(added.ParentId == target.Id && added.SeasonId == target.Id && added.SeriesId == series.Id
                && added.IndexNumber == 2 && added.ParentIndexNumber == 4, "wrong episode identity");
            var repeat = await h.Finish(h.Start(false, directory));
            Assert(repeat.GetProperty("Created").GetArrayLength() == 0, repeat.ToString());
        }
        else Assert(result.GetProperty("Message").GetString()!.Contains("其它季存在剧集归属冲突"), result.ToString());
    });
}

await Test("native refresh saves only created identity, preserves import on failure and skips offline/repeat", async () =>
{
    var h = New();
    h.Providers.Refresh = (item, options, token) =>
    {
        Throws<RepositoryProtectionException>(() => h.Guarded.SaveItems([h.Unrelated], token));
        Throws<RepositoryProtectionException>(() => h.Guarded.SaveImages(h.Unrelated));
        Throws<RepositoryProtectionException>(() => h.Guarded.DeleteItem([item.Id]));
        var parent = item.ParentId;
        item.ParentId = Guid.NewGuid();
        Throws<RepositoryProtectionException>(() => h.Guarded.SaveItems([item], token));
        Throws<RepositoryProtectionException>(() => h.Guarded.SaveImages(item));
        item.ParentId = parent;
        item.ImageInfos = [new ItemImageInfo { Path = "mock-image", Type = ImageType.Primary }];
        h.Guarded.SaveImages(item);
        h.Guarded.SaveItems([item], token);
        h.Guarded.ReattachUserDataAsync(item, token).GetAwaiter().GetResult();
        return Task.FromResult(ItemUpdateType.ImageUpdate);
    };
    var first = await h.Run(true);
    Assert(first.GetProperty("State").GetString() == "Completed" && h.Providers.Refreshed.Count == 1
        && first.GetProperty("NativeRefresh")[0].GetProperty("HasPrimaryImage").GetBoolean(), first.ToString());
    await h.Run(true);
    Assert(h.Providers.Refreshed.Count == 1 && h.Unrelated.Name == "untouched", "existing refresh");
    var offline = New();
    await offline.Run(false);
    Assert(offline.Providers.Refreshed.Count == 0, "offline native call");
    var failure = New();
    failure.Providers.Refresh = (item, options, token) => throw new IOException("mock image failure");
    var status = await failure.Run(true);
    Assert(status.GetProperty("State").GetString() == "Completed" && failure.Created.Count == 1
        && status.GetProperty("NativeRefresh")[0].GetProperty("Outcome").GetString() == "Failed", status.ToString());
});

await Test("native refresh targets follow new series, season and episode hierarchy", async () =>
{
    var h = new Harness(Path.Combine(fixtureRoot, Guid.NewGuid().ToString("N")), television: true);
    var firstDirectory = Path.Combine(h.Target, "Season 01");
    Directory.CreateDirectory(firstDirectory);
    File.WriteAllBytes(Path.Combine(firstDirectory, "Show S01E01.mkv"), [0]);
    h.Providers.Refresh = (item, options, token) =>
    {
        // Simulate native BeforeSaveInternal changing the Series key after children were saved.
        if (item is MediaBrowser.Controller.Entities.TV.Series) item.PresentationUniqueKey = "provider-final-key";
        else
        {
            var key = item is MediaBrowser.Controller.Entities.TV.Season s ? s.SeriesPresentationUniqueKey
                : ((MediaBrowser.Controller.Entities.TV.Episode)item).SeriesPresentationUniqueKey;
            Assert(key == "provider-final-key", "child refresh saw stale series key");
        }
        h.Guarded.SaveItems([item], token);
        h.Guarded.SaveImages(item);
        return Task.FromResult(ItemUpdateType.ImageUpdate);
    };
    var initial = await h.Finish(h.Start(true, firstDirectory));
    Assert(initial.GetProperty("State").GetString() == "Completed"
        && h.Providers.Refreshed.SequenceEqual(h.Created.Select(item => item.Id)), initial.ToString());
    var series = h.Created.OfType<MediaBrowser.Controller.Entities.TV.Series>().Single();
    var season = h.Created.OfType<MediaBrowser.Controller.Entities.TV.Season>().Single();
    Assert(season.SeriesPresentationUniqueKey == series.GetPresentationUniqueKey()
        && season.PresentationUniqueKey == "provider-final-key-001"
        && h.Created.OfType<MediaBrowser.Controller.Entities.TV.Episode>().All(e => e.SeriesPresentationUniqueKey == series.GetPresentationUniqueKey())
        && series.GetSeasons(null, new MediaBrowser.Controller.Dto.DtoOptions(false)).Any(item => item.Id == season.Id),
        "new season not present in native unfiltered query");
    var secondDirectory = Path.Combine(h.Target, "Season 02");
    Directory.CreateDirectory(secondDirectory);
    File.WriteAllBytes(Path.Combine(secondDirectory, "Show S02E01.mkv"), [0]);
    var before = h.Providers.Refreshed.Count;
    await h.Finish(h.Start(true, secondDirectory));
    Assert(h.Providers.Refreshed.Skip(before).SequenceEqual(h.Created.TakeLast(2).Select(item => item.Id)), "old series refreshed");
    File.WriteAllBytes(Path.Combine(secondDirectory, "Show S02E02.mkv"), [0]);
    before = h.Providers.Refreshed.Count;
    await h.Finish(h.Start(true, secondDirectory));
    Assert(h.Providers.Refreshed.Skip(before).SequenceEqual(new[] { h.Created[^1].Id }), "old season refreshed");
});

await Test("persisted Series key survives native failure and protection details identify the rejected write", async () =>
{
    var h = new Harness(Path.Combine(fixtureRoot, Guid.NewGuid().ToString("N")), television: true);
    var directory = Path.Combine(h.Target, "Season 04");
    Directory.CreateDirectory(directory);
    File.WriteAllBytes(Path.Combine(directory, "Show S04E01.mkv"), [0]);
    File.WriteAllBytes(Path.Combine(directory, "Show S04E02.mkv"), [0]);
    h.Providers.Refresh = (item, options, token) =>
    {
        if (item is MediaBrowser.Controller.Entities.TV.Series)
        {
            item.PresentationUniqueKey = "persisted-before-failure";
            h.Guarded.SaveItems([item], token);
            h.Guarded.SaveImages(h.Unrelated);
        }
        return Task.FromResult(ItemUpdateType.None);
    };
    var result = await h.Finish(h.Start(true, directory));
    var failure = result.GetProperty("NativeRefresh")[0];
    Assert(failure.GetProperty("Outcome").GetString() == "Failed"
        && failure.GetProperty("RepositoryFailure").GetString()!.Contains("SaveImages")
        && failure.GetProperty("RepositoryFailure").GetString()!.Contains(h.Unrelated.Id.ToString())
        && failure.GetProperty("StackTrace").GetString()!.Contains("RepositoryGuard"), result.ToString());
    var series = h.Created.OfType<MediaBrowser.Controller.Entities.TV.Series>().Single();
    Assert(series.GetSeasons(null, new MediaBrowser.Controller.Dto.DtoOptions(false)).Single().IndexNumber == 4
        && h.Created.OfType<MediaBrowser.Controller.Entities.TV.Episode>().All(e => e.SeriesPresentationUniqueKey == "persisted-before-failure")
        && h.Unrelated.Name == "untouched", "partial native failure left stale children");
});

await Test("NFO junction path remains fatal", () =>
{
    var h=New();File.WriteAllText(h.Nfo,"<movie/>");var link=Path.Combine(fixtureRoot,"junction-"+Guid.NewGuid().ToString("N"));
    var command="New-Item -ItemType Junction -Path '"+link.Replace("'","''")+"' -Target '"+h.Target.Replace("'","''")+"' | Out-Null";
    var start=new System.Diagnostics.ProcessStartInfo("pwsh"){UseShellExecute=false,CreateNoWindow=true};start.ArgumentList.Add("-NoProfile");start.ArgumentList.Add("-EncodedCommand");start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));
    using var process=System.Diagnostics.Process.Start(start)!;process.WaitForExit();
    Assert(process.ExitCode==0 && File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint),"fixture junction unavailable");
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    var pick = FolderPicker.Pick(fixtureRoot, link, default, (start, _) =>
        start == fixtureRoot ? link : throw new InvalidOperationException("junction was used as default")).GetAwaiter().GetResult();
    Assert(pick is { Status: "Error", Message: var message } && message!.Contains("重解析点"), "picker rejects a junction");
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
    public ProviderStub Providers => providerStub;
    public int Probes;
    private readonly ProviderStub providerStub = new();
    public IMetadataProvider<Movie>[] Selected { get => providerStub.Selected; set => providerStub.Selected = value; }
    public IItemRepository Guarded {get;}
    private readonly Importer importer;
    public Harness(string directory, bool television = false)
    {
        var root=Path.Combine(directory,television ? "tv" : "movies");Target=Path.Combine(root,"Example Movie (2001)");Directory.CreateDirectory(Target);if (!television) File.WriteAllBytes(Path.Combine(Target,"video.mkv"),[0]);Nfo=Path.Combine(Target,"movie.nfo");
        var physical=new Folder{Id=Guid.NewGuid(),Path=root,Name="root"};var collection=new CollectionFolder{Id=Guid.NewGuid(),PhysicalFolderIds=[physical.Id]};
        Store[physical.Id]=physical;Store[collection.Id]=collection;Store[Unrelated.Id]=Unrelated;
        var inner=Stub.Make<IItemRepository>((m,a)=>m.Name switch {
            "RetrieveItem"=>Store.GetValueOrDefault((Guid)a![0]!),
            "GetItemList"=>Query((InternalItemsQuery)a![0]!),
            "SaveItems"=>Save((IReadOnlyList<BaseItem>)a![0]!),
            "SaveImages"=>null,
            _=>throw new Exception("Unexpected repository call "+m.Name)});
        Guarded=DispatchProxy.Create<IItemRepository,RepositoryGuard>();((RepositoryGuard)(object)Guarded).Inner=inner;
        var library=Stub.Make<ILibraryManager>((m,a)=>m.Name switch {
            "GetVirtualFolders"=>new List<VirtualFolderInfo>{new(){ItemId=collection.Id.ToString(),Locations=[root],CollectionType=television ? CollectionTypeOptions.tvshows : CollectionTypeOptions.movies,LibraryOptions=new(){PreferredMetadataLanguage="en",MetadataCountryCode="US"}}},
            "ResolvePaths"=> ((IEnumerable<MediaBrowser.Model.IO.FileSystemMetadata>)a![0]!).Select(entry =>
                Resolve(entry, (Folder)a[2]!, television)),
            "FindByPath"=>Store.Values.FirstOrDefault(x=>x.Path==(string)a![0]!),
            "GetItemList"=>Query((InternalItemsQuery)a![0]!),
            "GetLibraryOptions"=>new LibraryOptions(),
            "GetItemById"=>Store.GetValueOrDefault((Guid)a![0]!),
            "GetNewItemId"=>new Guid(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes((string)a![0]!))),
            "CreateItem"=>Create((BaseItem)a![0]!),
            _=>throw new Exception("Unexpected library call "+m.Name)});
        var providers=providerStub;
        var encoder=Stub.Make<IMediaEncoder>((m,a)=>{if(m.Name!="GetMediaInfo")throw new Exception("Unexpected encoder call "+m.Name);Probes++;return Task.FromResult(new MediaInfo{Container="matroska",MediaStreams=[]});});
        var streams=Stub.Make<IMediaStreamRepository>((m,a)=>{if(m.Name!="SaveMediaStreams")throw new Exception("Unexpected streams call "+m.Name);return null;});
        var config=Stub.Make<IServerConfigurationManager>((m,a)=>m.Name=="get_Configuration"?new ServerConfiguration():throw new Exception("Unexpected config call "+m.Name));
        BaseItem.FileSystem = Stub.Make<MediaBrowser.Model.IO.IFileSystem>((m,a) => m.Name == "IsPathFile"
            ? Path.IsPathRooted((string)a![0]!) : throw new Exception("Unexpected filesystem call " + m.Name));
        BaseItem.LibraryManager=library;BaseItem.ItemRepository=Guarded;BaseItem.ConfigurationManager=config;BaseItem.Logger=NullLogger<BaseItem>.Instance;
        importer=new(library,Guarded,providers,encoder,streams,new NamingOptions(),config);
    }
    private static BaseItem Resolve(MediaBrowser.Model.IO.FileSystemMetadata entry, Folder parent, bool television)
    {
        if (!television) return new Movie { Path = Path.Combine(entry.FullName, "video.mkv"), Name = "Example Movie", ProductionYear = 2001 };
        if (!entry.IsDirectory) return new MediaBrowser.Controller.Entities.TV.Episode { Path = entry.FullName, Name = entry.Name };
        if (parent is MediaBrowser.Controller.Entities.TV.Series series)
            return new MediaBrowser.Controller.Entities.TV.Season { Path = entry.FullName, Name = entry.Name, SeriesId = series.Id,
                IndexNumber = Emby.Naming.TV.SeasonPathParser.Parse(entry.FullName, series.Path, true, true).SeasonNumber };
        return new MediaBrowser.Controller.Entities.TV.Series { Path = entry.FullName, Name = entry.Name };
    }
    private List<BaseItem> Query(InternalItemsQuery q)=>Store.Values.Where(x=>(q.SeriesPresentationUniqueKey is null || (x is MediaBrowser.Controller.Entities.TV.Season season && season.SeriesPresentationUniqueKey == q.SeriesPresentationUniqueKey) || (x is MediaBrowser.Controller.Entities.TV.Episode episode && episode.SeriesPresentationUniqueKey == q.SeriesPresentationUniqueKey)) && (q.IncludeItemTypes.Length == 0 || q.IncludeItemTypes.Any(type => type.ToString() == x.GetType().Name)) && (q.Path is null || x.Path==q.Path) && (q.ParentId==Guid.Empty || x.ParentId==q.ParentId) && (!q.IndexNumber.HasValue || x.IndexNumber==q.IndexNumber)).ToList();
    private object? Save(IReadOnlyList<BaseItem> items){foreach(var item in items)Store[item.Id]=item;return null;}
    private object? Create(BaseItem item){Guarded.SaveItems([item],CancellationToken.None);Created.Add(item);return null;}
    public Guid Start(bool fetch, string? path = null)=>JsonSerializer.SerializeToElement(importer.Start(new(path ?? Target,FetchMetadata:fetch))).GetProperty("OperationId").GetGuid();
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
