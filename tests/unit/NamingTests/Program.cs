using ScopedImport;
using Emby.Naming.Common;
var root = Path.Combine(Path.GetTempPath(), "ScopedImport-naming-fixture", "名称测试");
var options = new NamingOptions();
var count = 0;
void Good(string path, int season, int episode, string? seasonDirectory = null, int? nfoSeason = null, int? nfoEpisode = null)
{
    var parsed = EpisodeLayout.Resolve(Path.Combine(root,path),root,nfoSeason,nfoEpisode,[],options);
    if (parsed.Season != season || parsed.Episode != episode || parsed.SeasonDirectory != (seasonDirectory is null ? null : Path.Combine(root, seasonDirectory)))
        throw new Exception(path + " => " + parsed);
    count++;
}
void Bad(string path, int[]? old = null, int? nfoSeason = null)
{
    try { EpisodeLayout.Resolve(Path.Combine(root,path),root,nfoSeason,null,old ?? [],options); }
    catch (IOException) { count++; return; }
    throw new Exception("Expected rejection: " + path);
}
Good(@"E01「测试标题」 [BDRip][avc_aacx2_chap][1080p]\(7E562D56).mp4",1,1);
Good(@"E12「测试副标题」 [BDRip][1080p]\(ABCDEF12).mkv",1,12);
Good(@"Season 02\E01「测试标题」\(7E562D56).mp4",2,1,"Season 02");
Good(@"Season 01\Show.S01E02.mkv",1,2,"Season 01");
Good(@"Show.S02E03.mkv",2,3);
Good(@"E01.mp4",1,1);
Good(@"Season 00\Show.S00E01.mkv",0,1,"Season 00");
Good(@"(7E562D56).mp4",1,3,null,1,3);
Bad(@"(7E562D56).mp4");
Bad(@"E01\Show.S01E02.mp4");
Bad(@"Season 02\Show.S01E01.mp4");
Bad(@"E01\(7E562D56).mp4",[1,2]);
Bad(@"Show.S01E01-E02.mkv");
Bad(@"Show.S01E01.mkv",null,2);
Console.WriteLine($"PASS: {count} pure Jellyfin naming/layout cases (no filesystem access)");
