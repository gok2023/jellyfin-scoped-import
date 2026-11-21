using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;

namespace ScopedImport;

// Resolution can inspect the checked inventory without enumerating ancestors or siblings.
internal sealed class ResolutionDirectory : IDirectoryService
{
    private readonly Dictionary<string, FileSystemMetadata> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<FileSystemMetadata>> children = new(StringComparer.OrdinalIgnoreCase);

    public FileSystemMetadata Add(string path, bool directory)
    {
        if (entries.TryGetValue(path, out var existing)) return existing;
        var entry = new FileSystemMetadata
        {
            FullName = path, Name = Path.GetFileName(path), Exists = true, IsDirectory = directory,
            LastWriteTimeUtc = File.GetLastWriteTimeUtc(path)
        };
        entries.Add(path, entry);
        var parent = Path.GetDirectoryName(path)!;
        if (!children.TryGetValue(parent, out var siblings)) children[parent] = siblings = [];
        siblings.Add(entry);
        return entry;
    }

    public FileSystemMetadata[] GetFileSystemEntries(string path) =>
        children.TryGetValue(path, out var items) ? items.ToArray() : [];
    public List<FileSystemMetadata> GetDirectories(string path) => GetFileSystemEntries(path).Where(e => e.IsDirectory).ToList();
    public List<FileSystemMetadata> GetFiles(string path) => GetFileSystemEntries(path).Where(e => !e.IsDirectory).ToList();
    public FileSystemMetadata? GetFile(string path) => GetFileSystemEntry(path) is { IsDirectory: false } file ? file : null;
    public FileSystemMetadata? GetDirectory(string path) => GetFileSystemEntry(path) is { IsDirectory: true } directory ? directory : null;
    public FileSystemMetadata? GetFileSystemEntry(string path) => entries.GetValueOrDefault(path);
    public IReadOnlyList<string> GetFilePaths(string path) => GetFilePaths(path, false);
    public IReadOnlyList<string> GetFilePaths(string path, bool clearCache) => GetFilePaths(path, clearCache, false);
    public IReadOnlyList<string> GetFilePaths(string path, bool clearCache, bool sort)
    {
        var paths = GetFiles(path).Select(e => e.FullName);
        return (sort ? paths.Order(StringComparer.OrdinalIgnoreCase) : paths).ToArray();
    }
    // This inventory is a fixed, checked snapshot; invalidation must not enumerate new paths.
    public void Invalidate(string path) { }
    public void Move(string source, string destination) => throw new IOException("定向入库目录服务禁止移动媒体文件或目录。");
    public bool IsAccessible(string path) => GetDirectory(path) is not null;
}
