using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace ScopedImport;

public sealed class Operation(Guid id)
{
    public Guid Id { get; } = id;
    public string State { get; set; } = "Queued";
    public string Message { get; set; } = "等待处理";
    public List<object> Created { get; } = [];
    public List<string> Skipped { get; } = [];
    public List<string> EnumeratedDirectories { get; } = [];
    public List<string> ReadFiles { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<ProviderAttempt> MetadataProviders { get; } = [];
    public List<object> Movies { get; } = [];
    internal Dictionary<string, XDocument?> NfoDocuments { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal bool Finished { get; set; }

}

public sealed record ProviderAttempt(Guid ItemId, string Type, string Provider, string Outcome);

internal sealed record Pending(BaseItem Item, Folder Parent, string? Nfo, string? Cover, Series? Series, Season? Season);

public sealed class Importer(ILibraryManager library, IItemRepository repository, IProviderManager providers,
    IMediaEncoder encoder, IMediaStreamRepository streams, Emby.Naming.Common.NamingOptions naming,
    MediaBrowser.Controller.Configuration.IServerConfigurationManager configuration)
{
    private readonly ConcurrentDictionary<Guid, Operation> operations = new();
    private readonly SemaphoreSlim gate = new(1);
    internal static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool Below(string path, string root) => path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public object Start(ImportRequest request)
    {
        if (!gate.Wait(0)) throw new InvalidOperationException("已有入库操作正在处理，请等待完成；无需重复提交。 ");
        if (operations.Count >= 50)
            foreach (var key in operations.Where(x => x.Value.State is "Completed" or "Rejected" or "Partial").Take(25).Select(x => x.Key)) operations.TryRemove(key, out _);
        var op = new Operation(Guid.NewGuid());
        operations[op.Id] = op;
        _ = Task.Run(async () =>
        {
            try { await Run(request, op); }
            catch (Exception ex)
            {
                lock (op) { op.State = op.Created.Count == 0 ? "Rejected" : "Partial"; op.Message = ex.Message; }
            }
            finally { lock (op) { op.NfoDocuments.Clear(); gate.Release(); op.Finished = true; } }
        });
        return new { OperationId = op.Id };
    }
    public object? Status(Guid id)
    {
        if (!operations.TryGetValue(id, out var op)) return null;
        lock (op) return new { op.Id, State = !op.Finished && op.State is ("Completed" or "Rejected" or "Partial") ? "Finishing" : op.State, op.Message, Created = op.Created.ToArray(), Skipped = op.Skipped.ToArray(),
            EnumeratedDirectories = op.EnumeratedDirectories.ToArray(), ReadFiles = op.ReadFiles.ToArray(), Warnings = op.Warnings.ToArray(), MetadataProviders = op.MetadataProviders.ToArray(), Movies = op.Movies.ToArray() };
    }
    private static void State(Operation op, string state, string message) { lock (op) { op.State = state; op.Message = message; } }

    private static FileAttributes Check(string path)
    {
        var full = Full(path);
        for (var part = full; !string.IsNullOrEmpty(part); part = Path.GetDirectoryName(part))
            if (File.GetAttributes(part).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("暂不支持链接/重解析路径：" + part);
        return File.GetAttributes(full);
    }

    private async Task Run(ImportRequest request, Operation op)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var ct = deadline.Token;
        if (repository is not RepositoryGuard) throw new InvalidOperationException("只追加保护未启用");
        if (string.IsNullOrWhiteSpace(request.Path) || !Path.IsPathFullyQualified(request.Path)
            || request.Path.StartsWith(@"\\", StringComparison.Ordinal) || request.Path[2..].Contains(':')
            || request.Path.IndexOfAny(['*', '?']) >= 0) throw new IOException("请输入本机挂载盘上的完整路径");
        var target = Full(request.Path);
        if (target[Path.GetPathRoot(target)!.Length..].Split(Path.DirectorySeparatorChar)
            .Any(p => p.EndsWith(' ') || p.EndsWith('.') || Regex.IsMatch(p, @"(?i)^(?:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)")))
            throw new IOException("拒绝 Windows 路径别名或设备名称");
        var matches = library.GetVirtualFolders().SelectMany(l => l.Locations.Select(p => (Library: l, Root: Full(p))))
            .Where(x => Below(target, x.Root)).ToList();
        if (request.LibraryId is { } selected) matches = matches.Where(x => Guid.Parse(x.Library.ItemId) == selected).ToList();
        if (matches.Count != 1) throw new IOException("未唯一匹配媒体库，或输入的是媒体库根目录");
        var (folder, root) = matches[0];
        var kind = folder.CollectionType?.ToString().ToLowerInvariant();
        if (kind is not ("movies" or "tvshows")) throw new IOException("当前仅支持电影和电视剧类型媒体库（动漫请使用电视剧类型）");
        // No synthetic physical library roots: creating one could start monitoring.
        if (library.FindByPath(root, true) is not Folder physicalRoot || physicalRoot is AggregateFolder or UserRootFolder)
            throw new IOException("该媒体库物理根尚未建立索引；本插件不会为此触发全库扫描。请先完成媒体库初始化。");
        if (library.GetItemById(Guid.Parse(folder.ItemId)) is not CollectionFolder collection
            || !collection.PhysicalFolderIds.Contains(physicalRoot.Id)) throw new IOException("媒体库物理目录映射尚未建立，已停止");
        Check(target);
        State(op, "Planning", "检查目标子树与已有条目");
        var files = new List<string>();
        var inventory = new ResolutionDirectory();
        var watch = Stopwatch.StartNew();
        var entries = 0;
        void Walk(string directory, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (depth > 8 || watch.Elapsed > TimeSpan.FromSeconds(120)) throw new IOException("目录深度或枚举时间超过上限");
            Check(directory);
            if (Same(Path.GetFileName(directory), "VIDEO_TS") || Same(Path.GetFileName(directory), "BDMV")) throw new IOException("暂不支持光盘目录：" + directory);
            inventory.Add(directory, true);
            lock (op) op.EnumeratedDirectories.Add(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested();
                if (++entries > 10000 || watch.Elapsed > TimeSpan.FromSeconds(120)) throw new IOException("枚举数量或耗时超过上限");
                var path = Full(entry);
                if (!Below(path, target)) throw new IOException("枚举越界");
                if (Check(path).HasFlag(FileAttributes.Directory)) Walk(path, depth + 1);
                else { files.Add(path); inventory.Add(path, false); }
            }
        }
        if (Directory.Exists(target)) Walk(target, 0);
        else
        {
            files.Add(target);
            var directory = Path.GetDirectoryName(target)!;
            if (Same(directory, root)) throw new IOException("单文件目标不能直接位于媒体库根");
            inventory.Add(directory, true);
            lock (op) op.EnumeratedDirectories.Add(directory);
            foreach (var entry in Directory.EnumerateFiles(directory))
            {
                ct.ThrowIfCancellationRequested();
                if (++entries > 10000 || watch.Elapsed > TimeSpan.FromSeconds(120)) throw new IOException("同目录归组检查超过预算");
                var path = Full(entry);
                if (!Below(path, directory)) throw new IOException("枚举越界");
                Check(path);
                inventory.Add(path, false);
            }
        }
        if (files.Concat(Directory.Exists(target) ? [] : inventory.GetFilePaths(Path.GetDirectoryName(target)!)).Any(f => Path.GetExtension(f).ToLowerInvariant() is ".strm" or ".lnk" or ".mblink" or ".iso" or ".img" or ".vob"))
            throw new IOException("0.1 版暂不支持 ISO/光盘目录或链接媒体，未创建任何条目");
        var videos = files.Where(f => Emby.Naming.Video.VideoResolver.IsVideoFile(f, naming)).ToArray();
        if (videos.Length == 0) throw new IOException("目标内没有支持的视频文件");
        var pending = new List<Pending>();
        var options = folder.LibraryOptions;
        var planned = new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase);
        T Use<T>(string path, Func<T> factory, Folder parent, string? nfo, string? cover, Series? series = null, Season? season = null) where T : BaseItem
        {
            if (planned.TryGetValue(path, out var cached)) return (T)cached;
            var existing = library.FindByPath(path, null);
            if (existing is not null)
            {
                if (existing is not T typed) throw new IOException("已有条目类型冲突，已保留：" + path);
                if (typed.ParentId != parent.Id || typed.IsVirtualItem) throw new IOException("已有条目父级或虚拟状态冲突，已保留：" + path);
                planned[path] = typed;
                lock (op) { op.Skipped.Add(path); if (typed is Movie) MovieResult(op, typed, "ExistingSkipped", "ExistingSkipped"); }
                return typed;
            }
            var item = factory(); item.Path = path; item.Id = library.GetNewItemId(path, item.GetType()); item.SetParent(parent);
            item.DateCreated = DateTime.UtcNow; item.DateModified = File.GetLastWriteTimeUtc(path);
            planned[path] = item; pending.Add(new(item, parent, nfo, cover, series, season)); return item;
        }
        T Resolve<T>(string path, Folder parent, CollectionType type, bool directory, string? expectedPath = null) where T : BaseItem
        {
            var entry = inventory.Add(path, directory);
            using var readOnly = RepositoryGuard.ReadOnlyScope();
            var resolved = library.ResolvePaths([entry], inventory, parent, options, type).ToArray();
            if (resolved.Length == 0) throw new IOException("Jellyfin 未识别目标：" + path);
            if (resolved.Length != 1 || resolved[0] is not T item || !Same(item.Path ?? "", expectedPath ?? path)
                || item is Video { ExtraType: not null } or Video { VideoType: not VideoType.VideoFile })
                throw new IOException("Jellyfin 已识别，但插件无法安全创建此类型或归组：" + path);
            return item;
        }

        if (kind == "movies")
        {
            var movieDir = Directory.Exists(target) ? target : Path.GetDirectoryName(target)!;
            if (!Same(Path.GetDirectoryName(movieDir)!, root) || videos.Length != 1 || inventory.GetFilePaths(movieDir).Count(path => Emby.Naming.Video.VideoResolver.IsVideoFile(path, naming)) != 1 || !Same(Path.GetDirectoryName(videos[0])!, movieDir))
                throw new IOException("电影必须是媒体库下的独立单片目录，且只包含一个主视频；多版本/混合目录暂不支持");
            var video = videos[0];
            Use(video, () => Resolve<Movie>(movieDir, physicalRoot, CollectionType.movies, true, video), physicalRoot,
                Existing(Path.ChangeExtension(video, ".nfo"), Path.Combine(movieDir, "movie.nfo")), Existing(Path.Combine(movieDir, "poster.jpg"), Path.Combine(movieDir, "folder.jpg")));
        }
        else
        {
            var seriesPath = Path.Combine(root, Path.GetRelativePath(root, target).Split(Path.DirectorySeparatorChar)[0]);
            if (!Directory.Exists(seriesPath)) throw new IOException("缺少剧目目录");
            Check(seriesPath);
            var series = Use(seriesPath, () => Resolve<Series>(seriesPath, physicalRoot, CollectionType.tvshows, true), physicalRoot,
                Existing(Path.Combine(seriesPath, "tvshow.nfo")), Existing(Path.Combine(seriesPath, "poster.jpg"), Path.Combine(seriesPath, "folder.jpg")));
            var episodeKeys = new HashSet<(Guid Parent, int Episode)>();
            var numberedSeasons = new Dictionary<int, Season>();
            var episodeNames = new Emby.Naming.TV.EpisodeResolver(naming);

            foreach (var group in videos.GroupBy(video => Path.GetDirectoryName(video)!, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => Same(group.Key, seriesPath)))
            {
                var directory = group.Key;
                Season? directorySeason = null;
                if (!Same(directory, seriesPath))
                {
                    if (!Same(Path.GetDirectoryName(directory)!, seriesPath))
                        throw new IOException("当前插件无法安全创建嵌套剧集目录：" + directory);
                    if (naming.AllExtrasTypesFolderNames.ContainsKey(Path.GetFileName(directory)))
                        throw new IOException("当前插件无法安全创建 Jellyfin 额外内容目录：" + directory);
                    var candidate = Resolve<Season>(directory, series, CollectionType.tvshows, true);
                    if (candidate.IndexNumber is int number
                        && (numberedSeasons.ContainsKey(number) || repository.GetItemList(new InternalItemsQuery
                            { ParentId = series.Id, IndexNumber = number, IncludeItemTypes = [BaseItemKind.Season] })
                            .Any(s => !Same(s.Path ?? "", directory))))
                        throw new IOException("季编号冲突，保留已有条目：" + directory);
                    directorySeason = Use(directory, () => candidate, series, Existing(Path.Combine(directory, "season.nfo")), null, series);
                    if (directorySeason.IndexNumber != candidate.IndexNumber || directorySeason.SeriesId != series.Id)
                        throw new IOException("已有季编号或父级冲突：" + directory);
                    if (candidate.IndexNumber is int seasonNumber) numberedSeasons.Add(seasonNumber, directorySeason);
                }

                var parent = (Folder?)directorySeason ?? series;
                var paths = group.ToHashSet(StringComparer.OrdinalIgnoreCase);
                BaseItem[] resolved;
                using (RepositoryGuard.ReadOnlyScope())
                    resolved = library.ResolvePaths(inventory.GetFiles(directory), inventory, parent, options, CollectionType.tvshows).ToArray();
                var episodes = new Dictionary<string, Episode>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in resolved)
                {
                    if (!paths.Contains(item.Path ?? "")) continue;
                    if (item is not Episode { ExtraType: null, VideoType: VideoType.VideoFile } episode
                        || episode.AdditionalParts.Length > 0 || episode.LocalAlternateVersions.Length > 0
                        || !episodes.TryAdd(episode.Path, episode))
                        throw new IOException("Jellyfin 已识别，但插件无法安全创建此类型或归组：" + directory);
                }

                foreach (var video in group)
                {
                    if (!episodes.TryGetValue(video, out var episode))
                        throw new IOException("Jellyfin 未识别为独立剧集，可能是额外内容或归组成员：" + video);
                    var parsed = episodeNames.Resolve(video, false, isOptimistic: false);
                    episode.IndexNumber ??= parsed?.EpisodeNumber;
                    var seasonNumber = episode.ParentIndexNumber ?? parsed?.SeasonNumber;
                    if (parsed?.EndingEpisodeNumber is int end && end != episode.IndexNumber)
                        throw new IOException("当前插件无法安全创建跨集文件：" + video);
                    var season = directorySeason;
                    if (season is null && (seasonNumber ?? (episode.IndexNumber.HasValue || episode.PremiereDate.HasValue ? 1 : null)) is int flatNumber)
                    {
                        seasonNumber = flatNumber;
                        if (!numberedSeasons.TryGetValue(flatNumber, out season))
                        {
                            var old = repository.GetItemList(new InternalItemsQuery { ParentId = series.Id, IndexNumber = flatNumber, IncludeItemTypes = [BaseItemKind.Season] }).OfType<Season>().ToArray();
                            if (old.Length > 1 || old.Any(s => s.IsVirtualItem || s.SeriesId != series.Id))
                                throw new IOException("已有季记录冲突或为缺失季，已保留");
                            season = old.SingleOrDefault();
                            if (season is null)
                            {
                                var name = "Season " + flatNumber;
                                season = new Season { Name = name, IndexNumber = flatNumber, SeriesId = series.Id, SeriesName = series.Name,
                                    Id = library.GetNewItemId(series.Id + flatNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) + name, typeof(Season)),
                                    DateCreated = DateTime.UtcNow };
                                season.SetParent(series);
                                pending.Add(new(season, series, null, null, series, null));
                            }
                            numberedSeasons.Add(flatNumber, season);
                        }
                    }
                    if (season?.IndexNumber is int expected && seasonNumber is int actual && expected != actual)
                        throw new IOException("剧集与父季编号冲突：" + video);
                    parent = (Folder?)season ?? series;
                    episode.ParentIndexNumber = season?.IndexNumber ?? seasonNumber;
                    episode.SeasonId = season?.Id ?? Guid.Empty;
                    episode.SeriesId = series.Id;
                    if (episode.IndexNumber is int number &&
                        (!episodeKeys.Add((parent.Id, number)) || repository.GetItemList(new InternalItemsQuery
                            { ParentId = parent.Id, IndexNumber = number, IncludeItemTypes = [BaseItemKind.Episode] })
                            .Any(e => !Same(e.Path ?? "", video))))
                        throw new IOException("剧集编号冲突，保留已有条目：" + video);
                    var actualEpisode = Use(video, () => episode, parent, Existing(Path.ChangeExtension(video, ".nfo")), null, series, season);
                    if (actualEpisode.SeriesId != series.Id || actualEpisode.SeasonId != episode.SeasonId
                        || (actualEpisode.IndexNumber is int oldNumber && episode.IndexNumber is int newNumber && oldNumber != newNumber))
                        throw new IOException("已有剧集父级或编号冲突：" + video);
                }
            }
        }
        State(op, "Metadata", "仅获取新条目的元数据和媒体信息");
        var probeResults = new Dictionary<Guid, MediaInfo>();
        var metadataResults = new Dictionary<Guid, string>();
        foreach (var entry in pending)
        {
            ct.ThrowIfCancellationRequested();
            var item = entry.Item;
            var localTitle = entry.Nfo is not null && ApplyNfo(item, entry.Nfo, op);
            metadataResults[item.Id] = request.ShouldFetchMetadata
                ? await FetchMetadata(entry, options, op, ct, localTitle) : "LocalOnly";
            if (entry.Cover is not null)
            {
                Check(entry.Cover);
                item.ImageInfos = [new ItemImageInfo { Path = entry.Cover, Type = ImageType.Primary, DateModified = File.GetLastWriteTimeUtc(entry.Cover) }];
            }
            if (item is Video video)
            {
                Check(video.Path); lock (op) op.ReadFiles.Add(video.Path);
                using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct); probeTimeout.CancelAfter(TimeSpan.FromSeconds(60));
                var info = await encoder.GetMediaInfo(new MediaInfoRequest { MediaType = DlnaProfileType.Video, ExtractChapters = false,
                    MediaSource = new MediaSourceInfo { Path = video.Path, Protocol = MediaProtocol.File } }, probeTimeout.Token);
                video.RunTimeTicks = info.RunTimeTicks; video.Container = info.Container; video.Size = new FileInfo(video.Path).Length;
                video.DefaultVideoStreamIndex = info.MediaStreams?.FirstOrDefault(s => s.Type == MediaStreamType.Video)?.Index;
                probeResults[item.Id] = info;
            }
        }
        // Preflight the entire selected subtree again BEFORE the first item save.
        foreach (var file in files) Check(file);
        State(op, "Creating", "只追加缺失条目");
        foreach (var entry in pending)
        {
            ct.ThrowIfCancellationRequested();
            var item = entry.Item;
            if (!string.IsNullOrEmpty(item.Path)) Check(item.Path);
            lock (RepositoryGuard.SaveGate)
            {
                // Parent may have been created earlier by this operation. Resolve it
                // from the normal cache/repository; do not refresh or update it.
                if (entry.Series is { } series)
                {
                    if (item is Season s) { s.SeriesName = series.Name; s.SeriesPresentationUniqueKey = series.GetPresentationUniqueKey(); }
                    if (item is Episode e) { e.SeriesName = series.Name; e.SeriesPresentationUniqueKey = series.GetPresentationUniqueKey(); e.SeasonName = entry.Season?.Name; }
                }
                item.PresentationUniqueKey = item.CreatePresentationUniqueKey();
                using (RepositoryGuard.AppendScope([item.Id])) library.CreateItem(item, entry.Parent);
                lock (op)
                {
                    op.Created.Add(new { item.Id, Type = item.GetType().Name, item.Path, item.Name, item.ParentId, item.IndexNumber, item.ParentIndexNumber });
                    if (item is Movie) MovieResult(op, item, "Created", metadataResults[item.Id]);
                }
                // Only this operation's freshly created ID, while holding the gate.
                if (probeResults.TryGetValue(item.Id, out var info)) streams.SaveMediaStreams(item.Id, info.MediaStreams, ct);
            }
        }
        State(op, "Completed", pending.Count == 0 ? "目标条目已存在，未刷新或修改" : $"已追加 {pending.Count} 个条目；未请求媒体库扫描或祖先刷新");
    }

    private static string? Existing(params string[] paths)
    {
        foreach (var path in paths)
        {
            try { if (!Check(path).HasFlag(FileAttributes.Directory)) return path; }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return null;
    }
    private static void MovieResult(Operation op, BaseItem item, string entryState, string metadataStatus) =>
        op.Movies.Add(new { ItemId = item.Id, item.Name, item.Path, EntryState = entryState, MetadataStatus = metadataStatus,
            FullMetadataRefresh = false, ManualIdentification = "打开电影详情后点击更多菜单中的“刮削元数据”，输入名称、年份或提供器 ID；使用 Jellyfin Web 管理员登录。" });

    // Content failures alone may degrade. Path/access/size/DTD errors escape to the job boundary.
    internal static XDocument? ReadNfo(string path, Operation op)
    {
        if (op.NfoDocuments.TryGetValue(path, out var cached)) return cached;
        Check(path);
        const int limit = 2_000_000;
        using var file = File.OpenRead(path);
        if (file.Length > limit) throw new NfoSafetyException("NFO 超过 2MB 上限");
        var bytes = new byte[limit + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = file.Read(bytes, length, bytes.Length - length);
            if (read == 0) break;
            length += read;
        }
        if (length > limit) throw new NfoSafetyException("NFO 超过 2MB 上限");
        lock (op) op.ReadFiles.Add(path);
        // Decode with BOM detection, also supporting XML's BOM-less UTF-16/32 signatures.
        var encoding = length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0 ? new System.Text.UTF32Encoding(true, true)
            : length >= 4 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0 ? System.Text.Encoding.UTF32
            : length >= 4 && bytes[0] == 0 && bytes[2] == 0 ? System.Text.Encoding.BigEndianUnicode
            : length >= 4 && bytes[1] == 0 && bytes[3] == 0 ? System.Text.Encoding.Unicode
            : System.Text.Encoding.UTF8;
        // XmlReader handles the XML encoding declaration; this preliminary read is only a DTD guard.
        using var content = new StreamReader(new MemoryStream(bytes, 0, length), encoding, true);
        var text = content.ReadToEnd();
        if (HasDoctype(text)) throw new NfoSafetyException("NFO 含禁止的 DTD/外部实体声明，已停止");
        XDocument? document = null;
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(bytes, 0, length), new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = limit });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        { lock (op) op.Warnings.Add("NFO 为空、非 XML 或内容损坏，已忽略其元数据并继续基础入库：" + path); }
        op.NfoDocuments[path] = document;
        return document;
    }
    private static bool HasDoctype(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            var rest = text.AsSpan(index);
            // Literal text inside comments, CDATA and processing instructions is not a declaration.
            var end = rest.StartsWith("<!--", StringComparison.Ordinal) ? "-->"
                : rest.StartsWith("<![CDATA[", StringComparison.Ordinal) ? "]]>"
                : rest.StartsWith("<?", StringComparison.Ordinal) ? "?>" : null;
            if (end is not null)
            {
                var closing = text.IndexOf(end, index + 2, StringComparison.Ordinal);
                if (closing < 0) return false; // Malformed content is handled by XmlReader.
                index = closing + end.Length - 1;
            }
            else if (rest.StartsWith("<!DOCTYPE", StringComparison.Ordinal)) return true;
        }
        return false;
    }
    private static XElement? NfoRoot(string path, string expected, Operation op)
    {
        var root = ReadNfo(path, op)?.Root;
        if (root is null) return null;
        if (root.Name == expected) return root;
        var warning = "NFO 根类型不受支持（需要 " + expected + "），已忽略并继续基础入库：" + path;
        lock (op) { if (!op.Warnings.Contains(warning)) op.Warnings.Add(warning); }
        return null;
    }
    internal static int? NfoNumber(string? path, string field, Operation op) => path is not null && int.TryParse(NfoRoot(path, "episodedetails", op)?.Element(field)?.Value, out var number) ? number : null;
    internal static bool ApplyNfo(BaseItem item, string path, Operation op)
    {
        var expected = item switch { Movie => "movie", Series => "tvshow", Season => "season", _ => "episodedetails" };
        var root = NfoRoot(path, expected, op);
        if (root is null) return false;
        var title = root.Element("title")?.Value;
        if (!string.IsNullOrWhiteSpace(title)) item.Name = title;
        item.Overview = root.Element("plot")?.Value;
        if (int.TryParse(root.Element("year")?.Value, out var year) && year > 0 && year <= 9999) item.ProductionYear = year;
        foreach (var id in root.Elements("uniqueid"))
        {
            var type = id.Attribute("type")?.Value.ToLowerInvariant();
            if (type is "tmdb" or "imdb" or "tvdb" && !string.IsNullOrWhiteSpace(id.Value)) item.ProviderIds[type == "tmdb" ? "Tmdb" : type == "imdb" ? "Imdb" : "Tvdb"] = id.Value.Trim();
        }
        foreach (var (tag, provider) in new[] { ("tmdbid", "Tmdb"), ("imdbid", "Imdb"), ("tvdbid", "Tvdb") })
            if (!string.IsNullOrWhiteSpace(root.Element(tag)?.Value)) item.ProviderIds[provider] = root.Element(tag)!.Value.Trim();
        return !string.IsNullOrWhiteSpace(title);
    }

    private async Task<string> FetchMetadata(Pending entry, LibraryOptions options, Operation op, CancellationToken ct, bool localTitle)
    {
        var item = entry.Item;
        ItemLookupInfo info = item switch { Movie => new MovieInfo(), Series => new SeriesInfo(), Season => new SeasonInfo(), _ => new EpisodeInfo() };
        info.Name = item.Name; info.Path = item.Path; info.ProviderIds = new(item.ProviderIds); info.Year = item.ProductionYear;
        info.MetadataLanguage = options.PreferredMetadataLanguage ?? configuration.Configuration.PreferredMetadataLanguage;
        info.MetadataCountryCode = options.MetadataCountryCode ?? configuration.Configuration.MetadataCountryCode;
        info.IndexNumber = item.IndexNumber; info.ParentIndexNumber = item.ParentIndexNumber;
        if (info is SeasonInfo si) { si.SeriesProviderIds = new(entry.Series!.ProviderIds); si.SeriesDisplayOrder = entry.Series.DisplayOrder; }
        if (info is EpisodeInfo ei) { ei.SeriesProviderIds = new(entry.Series!.ProviderIds); if (entry.Season is { } season) ei.SeasonProviderIds = new(season.ProviderIds); ei.SeriesDisplayOrder = entry.Series.DisplayOrder; }
        var before = op.MetadataProviders.Count;
        BaseItem? remote = item switch
        {
            Movie m => await Get(m, (MovieInfo)info, options, op, ct),
            Series s => await Get(s, (SeriesInfo)info, options, op, ct),
            Season s => await Get(s, (SeasonInfo)info, options, op, ct),
            Episode e => await Get(e, (EpisodeInfo)info, options, op, ct), _ => null
        };
        if (remote is null)
        {
            lock (op) op.Warnings.Add("未获取远程元数据，请手动识别：" + item.Name);
            var attempts = op.MetadataProviders.Skip(before).ToArray();
            return attempts.Length == 0 ? "ProvidersUnavailable" : attempts.Any(x => x.Outcome == "Timeout") ? "ProviderTimeout"
                : attempts.Any(x => x.Outcome == "Error") ? "ProviderFailed" : "NoMatch";
        }
        // Never pass a persisted item to a metadata service or merge into an ancestor.
        if (!localTitle
            && !string.IsNullOrEmpty(remote.Name)) item.Name = remote.Name;
        if (string.IsNullOrWhiteSpace(item.Overview)) item.Overview = remote.Overview; item.ProductionYear ??= remote.ProductionYear;
        item.PremiereDate ??= remote.PremiereDate; item.OfficialRating ??= remote.OfficialRating;
        item.CommunityRating ??= remote.CommunityRating; item.OriginalTitle ??= remote.OriginalTitle;
        item.Genres = remote.Genres; item.Studios = remote.Studios;
        foreach (var id in remote.ProviderIds) item.ProviderIds.TryAdd(id.Key, id.Value);
        return "FetchedFields";
    }
    private async Task<BaseItem?> Get<T, TInfo>(T item, TInfo info, LibraryOptions options, Operation op, CancellationToken ct)
        where T : BaseItem, IHasLookupInfo<TInfo>, new() where TInfo : ItemLookupInfo, new()
    {
        T? merged = null;
        // Jellyfin selects enabled providers and applies each library/type's order.
        // Only request returned metadata; never execute full metadata services.
        var selected = providers.GetMetadataProviders<T>(item, options).OfType<IRemoteMetadataProvider<T, TInfo>>().ToArray();
        foreach (var provider in selected)
        {
            ct.ThrowIfCancellationRequested();
            string outcome;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                using var readOnly = RepositoryGuard.ReadOnlyScope();
                // Do not abandon a provider task: an uncooperative provider must
                // not overlap the next provider or keep running after item creation.
                var result = await provider.GetMetadata(info, timeout.Token);
                timeout.Token.ThrowIfCancellationRequested();
                outcome = result.HasMetadata && result.Item is not null ? "Metadata" : "NoMetadata";
                if (result.HasMetadata && result.Item is { } data)
                {
                    merged ??= new T();
                    MergeMissing(merged, data);
                    foreach (var id in merged.ProviderIds) info.ProviderIds.TryAdd(id.Key, id.Value);
                }
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or UnauthorizedAccessException or System.Security.SecurityException
                or RepositoryProtectionException or NfoSafetyException) && !ct.IsCancellationRequested)
            {
                outcome = ex is OperationCanceledException or TimeoutException ? "Timeout" : "Error";
                lock (op) op.Warnings.Add(provider.Name + " 未完成：" + item.Name + "（" + ex.GetType().Name + "）；继续下一提供器。");
            }
            lock (op) op.MetadataProviders.Add(new ProviderAttempt(item.Id, typeof(T).Name, provider.Name, outcome));
        }
        return merged;
    }

    private static void MergeMissing(BaseItem target, BaseItem source)
    {
        if (string.IsNullOrWhiteSpace(target.Name)) target.Name = source.Name;
        if (string.IsNullOrWhiteSpace(target.Overview)) target.Overview = source.Overview;
        target.ProductionYear ??= source.ProductionYear; target.PremiereDate ??= source.PremiereDate;
        target.OfficialRating ??= source.OfficialRating; target.CommunityRating ??= source.CommunityRating;
        target.OriginalTitle ??= source.OriginalTitle;
        if (target.Genres.Length == 0) target.Genres = source.Genres;
        if (target.Studios.Length == 0) target.Studios = source.Studios;
        foreach (var id in source.ProviderIds) target.ProviderIds.TryAdd(id.Key, id.Value);
    }
}

internal sealed class NfoSafetyException(string message) : IOException(message);
