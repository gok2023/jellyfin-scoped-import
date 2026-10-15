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
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mkv", ".mp4", ".m4v", ".avi", ".ts", ".m2ts", ".mov", ".wmv" };
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
        var watch = Stopwatch.StartNew();
        var entries = 0;
        void Walk(string directory, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (depth > 8 || watch.Elapsed > TimeSpan.FromSeconds(120)) throw new IOException("目录深度或枚举时间超过上限");
            Check(directory);
            lock (op) op.EnumeratedDirectories.Add(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested();
                if (++entries > 10000 || watch.Elapsed > TimeSpan.FromSeconds(120)) throw new IOException("枚举数量或耗时超过上限");
                var path = Full(entry);
                if (!Below(path, target)) throw new IOException("枚举越界");
                if (Check(path).HasFlag(FileAttributes.Directory)) Walk(path, depth + 1);
                else files.Add(path);
            }
        }
        if (Directory.Exists(target)) Walk(target, 0); else files.Add(target);
        if (files.Any(f => Path.GetExtension(f).ToLowerInvariant() is ".strm" or ".lnk" or ".mblink" or ".iso" or ".vob"))
            throw new IOException("0.1 版暂不支持 ISO/光盘目录或链接媒体，未创建任何条目");
        var videos = files.Where(f => VideoExtensions.Contains(Path.GetExtension(f))).ToArray();
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
        if (kind == "movies")
        {
            var movieDir = Directory.Exists(target) ? target : Path.GetDirectoryName(target)!;
            if (!Same(Path.GetDirectoryName(movieDir)!, root) || videos.Length != 1 || !Same(Path.GetDirectoryName(videos[0])!, movieDir))
                throw new IOException("电影必须是媒体库下的独立单片目录，且只包含一个主视频；多版本/混合目录暂不支持");
            var video = videos[0];
            var parsed = Emby.Naming.Video.VideoResolver.CleanDateTime(Path.GetFileName(movieDir), naming);
            var movieName = Emby.Naming.Video.VideoResolver.TryCleanString(parsed.Name, naming, out var cleanName) ? cleanName : parsed.Name;
            Use(video, () => new Movie { Name = string.IsNullOrWhiteSpace(movieName) ? Path.GetFileName(movieDir) : movieName,
                ProductionYear = parsed.Year, VideoType = VideoType.VideoFile }, physicalRoot,
                Existing(Path.ChangeExtension(video, ".nfo"), Path.Combine(movieDir, "movie.nfo")), Existing(Path.Combine(movieDir, "poster.jpg"), Path.Combine(movieDir, "folder.jpg")));
        }
        else
        {
            var seriesPath = Path.Combine(root, Path.GetRelativePath(root, target).Split(Path.DirectorySeparatorChar)[0]);
            if (!Directory.Exists(seriesPath)) throw new IOException("缺少剧目目录");
            Check(seriesPath);
            var series = Use(seriesPath, () => new Series { Name = Path.GetFileName(seriesPath) }, physicalRoot,
                Existing(Path.Combine(seriesPath, "tvshow.nfo")), Existing(Path.Combine(seriesPath, "poster.jpg"), Path.Combine(seriesPath, "folder.jpg")));
            var seasons = new Dictionary<int, Season>();
            var episodeKeys = new HashSet<(int, int)>();
            var existingSeasonNumbers = repository.GetItemList(new InternalItemsQuery { ParentId = series.Id, IncludeItemTypes = [BaseItemKind.Season] })
                .Where(s => s.IndexNumber.HasValue).Select(s => s.IndexNumber!.Value).ToArray();
            foreach (var video in videos)
            {
                var nfo = Existing(Path.ChangeExtension(video, ".nfo"));
                var layout = EpisodeLayout.Resolve(video, seriesPath, NfoNumber(nfo, "season", op), NfoNumber(nfo, "episode", op), existingSeasonNumbers, naming);
                int? episodeNumber = layout.Episode, seasonNumber = layout.Season;
                var directory = layout.SeasonDirectory ?? seriesPath;
                if (layout.AssumedSeasonOne)
                    lock (op) { if (!op.Warnings.Contains("部分剧集没有季号，按 Jellyfin 规则归入第 1 季。")) op.Warnings.Add("部分剧集没有季号，按 Jellyfin 规则归入第 1 季。"); }
                if (episodeNumber < 0 || seasonNumber < 0 || !episodeKeys.Add((seasonNumber.Value, episodeNumber.Value)))
                    throw new IOException("季集编号无效或同一集有多个文件，未创建条目：" + video);
                if (!seasons.TryGetValue(seasonNumber.Value, out var season))
                {
                    if (Same(directory, seriesPath))
                    {
                        var old = repository.GetItemList(new InternalItemsQuery { ParentId = series.Id, IndexNumber = seasonNumber, IncludeItemTypes = [BaseItemKind.Season] }).OfType<Season>().ToArray();
                        if (old.Length > 1 || old.Any(s => s.IsVirtualItem)) throw new IOException("季记录冲突或为缺失季，保留已有记录并停止");
                        season = old.SingleOrDefault();
                        if (season is null)
                        {
                            var name = "Season " + seasonNumber.Value;
                            season = new Season { Name = name, IndexNumber = seasonNumber, SeriesId = series.Id, SeriesName = series.Name,
                                Id = library.GetNewItemId(series.Id + seasonNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + name, typeof(Season)),
                                IsVirtualItem = false, DateCreated = DateTime.UtcNow };
                            season.SetParent(series); pending.Add(new(season, series, null, null, series, null));
                        }
                    }
                    else
                    {
                        var sameNumber = repository.GetItemList(new InternalItemsQuery { ParentId = series.Id, IndexNumber = seasonNumber, IncludeItemTypes = [BaseItemKind.Season] });
                        if (sameNumber.Any(s => string.IsNullOrEmpty(s.Path) || !Same(s.Path, directory)))
                            throw new IOException("已有同编号季位于另一目录，保留并停止：" + directory);
                        season = Use(directory, () => new Season { Name = "Season " + seasonNumber.Value, IndexNumber = seasonNumber,
                            SeriesId = series.Id, SeriesName = series.Name, IsVirtualItem = false }, series, Existing(Path.Combine(directory, "season.nfo")), null, series);
                    }
                    seasons[seasonNumber.Value] = season;
                }
                if ((!Same(directory, seriesPath) && !Same(season.Path ?? "", directory)) || season.IndexNumber != seasonNumber)
                    throw new IOException("同一季出现目录或编号冲突：" + video);
                if (repository.GetItemList(new InternalItemsQuery { ParentId = season.Id, IndexNumber = episodeNumber, IncludeItemTypes = [BaseItemKind.Episode] })
                    .Any(e => string.IsNullOrEmpty(e.Path) || !Same(e.Path, video))) throw new IOException("已有同编号集位于另一文件，保留并停止：" + video);
                Use(video, () => new Episode { Name = layout.DisplayName, VideoType = VideoType.VideoFile,
                    IndexNumber = episodeNumber, ParentIndexNumber = seasonNumber, SeriesId = series.Id, SeasonId = season.Id,
                    SeriesName = series.Name, SeasonName = season.Name }, season, nfo, null, series, season);
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
                    if (item is Episode e) { e.SeriesName = series.Name; e.SeriesPresentationUniqueKey = series.GetPresentationUniqueKey(); e.SeasonName = entry.Season!.Name; }
                }
                item.PresentationUniqueKey = item.CreatePresentationUniqueKey();
                using (RepositoryGuard.AppendScope([item.Id])) library.CreateItem(item, entry.Parent);
                lock (op)
                {
                    op.Created.Add(new { item.Id, Type = item.GetType().Name, item.Path, item.Name, item.ParentId });
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
        if (info is EpisodeInfo ei) { ei.SeriesProviderIds = new(entry.Series!.ProviderIds); ei.SeasonProviderIds = new(entry.Season!.ProviderIds); ei.SeriesDisplayOrder = entry.Series.DisplayOrder; }
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
