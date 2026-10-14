using System.Text.RegularExpressions;
using Emby.Naming.Common;
using Emby.Naming.TV;
namespace ScopedImport;

public sealed record EpisodePlacement(int Season, int Episode, string? SeasonDirectory, string DisplayName, bool AssumedSeasonOne);

// Pure name parsing, without filesystem resolvers or parent enumeration.
public static class EpisodeLayout
{
    private static readonly Regex SeasonLabel = new(@"(?i)^(?:season[ ._-]*|s|第)(\d{1,3})(?:季)?$", RegexOptions.Compiled);
    private static readonly Regex EpisodeLabel = new(@"(?i)^(?:E|EP(?:ISODE)?[ ._-]*)(\d{1,4})(?![\p{L}\p{N}])", RegexOptions.Compiled);
    private static readonly Regex Checksum = new(@"(?i)^\(?[a-f0-9]{8}\)?$", RegexOptions.Compiled);
    private static readonly Regex SeasonEpisode = new(@"(?i)(?<![a-z0-9])S(?<s>\d{1,3})[ ._-]*E(?<e>\d{1,4})(?!\d)", RegexOptions.Compiled);
    public static EpisodePlacement Resolve(string video, string series, int? nfoSeason, int? nfoEpisode,
        IReadOnlyCollection<int> existingSeasons, NamingOptions naming)
    {
        var parts = Path.GetRelativePath(series, video).Split(Path.DirectorySeparatorChar);
        if (parts.Length > 3 || parts.Any(p => p == "..")) throw new IOException("不支持的剧集目录层级：" + video);
        var resolver = new EpisodeResolver(naming);
        (int? Season, int? Episode) Parse(string name, bool directory)
        {
            var stem = directory ? name : Path.GetFileNameWithoutExtension(name);
            if (Checksum.IsMatch(stem)) return (null, null);
            var explicitSeasonEpisode = SeasonEpisode.Match(stem);
            if (explicitSeasonEpisode.Success)
            {
                if (Regex.IsMatch(stem[(explicitSeasonEpisode.Index + explicitSeasonEpisode.Length)..], @"(?i)^[ ._-]*(?:E\d|S\d|\d)")) throw new IOException("暂不支持多集合并文件：" + video);
                return (int.Parse(explicitSeasonEpisode.Groups["s"].Value), int.Parse(explicitSeasonEpisode.Groups["e"].Value));
            }
            var parsed = resolver.Resolve(Path.Combine(Path.GetPathRoot(video)!, name), directory, isOptimistic: false);
            if (parsed?.EndingEpisodeNumber is { } end && end != parsed.EpisodeNumber) throw new IOException("暂不支持多集合并文件：" + video);
            var explicitEpisode = EpisodeLabel.Match(stem);
            return (parsed?.SeasonNumber, explicitEpisode.Success ? int.Parse(explicitEpisode.Groups[1].Value) : parsed?.EpisodeNumber);
        }
        var file = Parse(parts[^1], false);
        int? directorySeason = null, directoryEpisode = null, folderSeason = null;
        string? seasonPath = null;
        var display = Path.GetFileNameWithoutExtension(video);
        if (parts.Length > 1)
        {
            var label = SeasonLabel.Match(parts[0]);
            if (label.Success) { directorySeason = int.Parse(label.Groups[1].Value); seasonPath = Path.Combine(series, parts[0]); }
            if (parts.Length == 3 && !label.Success) throw new IOException("剧集子目录的父级不是季目录：" + video);
            if (parts.Length == 3 || !label.Success)
            {
                var parsed = Parse(parts[^2], true);
                if (parsed.Episode is null) throw new IOException("子目录未能识别集数：" + video);
                directoryEpisode = parsed.Episode; folderSeason = parsed.Season; display = parts[^2];
            }
        }
        int? Consistent(string kind, params int?[] values)
        {
            var unique = values.Where(v => v.HasValue).Select(v => v!.Value).Distinct().ToArray();
            if (unique.Length > 1) throw new IOException(kind + "信息冲突，未创建条目：" + video);
            return unique.Length == 1 ? unique[0] : null;
        }
        var episode = Consistent("集数", nfoEpisode, file.Episode, directoryEpisode);
        var season = Consistent("季数", nfoSeason, file.Season, folderSeason, directorySeason);
        if (episode is null) throw new IOException("无法识别集数；支持 S01E01、E01 剧集目录或 NFO：" + video);
        var assumed = season is null;
        if (assumed && existingSeasons.Any(s => s != 1)) throw new IOException("已有其他季但新文件没有季数，无法安全默认第一季：" + video);
        season ??= 1;
        if (season < 0 || episode < 0) throw new IOException("季集编号不能为负数：" + video);
        return new(season.Value, episode.Value, seasonPath, display, assumed);
    }
}
