using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace ScopedImport;

internal static class FolderPicker
{
    private static int active;
    private const StringComparison Comparison = StringComparison.OrdinalIgnoreCase;

    internal sealed record Result(string Status, string? Path = null, string? Message = null);

    [SupportedOSPlatform("windows")]
    internal static Task<Result> Pick(string configuredRoot, string? preferred, CancellationToken disconnected,
        Func<string, CancellationToken, string?> show)
    {
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
            return Task.FromResult(new Result("Busy", Message: "已有 Windows 文件夹选择窗口，请先完成或取消。"));

        var completion = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var thread = new Thread(() =>
            {
                Result result;
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(disconnected);
                    deadline.CancelAfter(TimeSpan.FromMinutes(10));
                    var token = deadline.Token;
                    token.ThrowIfCancellationRequested();
                    var root = Normalize(configuredRoot) ?? throw new ArgumentException("媒体库根目录无效。");
                    CheckDirectory(root, token);
                    var start = TryPreferred(root, preferred, token);
                    var selected = show(start, token);
                    token.ThrowIfCancellationRequested();
                    result = selected is null ? new("Cancelled") : new("Selected", ValidateSelection(root, selected, token));
                }
                catch (OperationCanceledException)
                {
                    result = disconnected.IsCancellationRequested ? new("Cancelled")
                        : new("Error", Message: "Windows 文件夹选择已超时，请重新打开或手动粘贴路径。");
                }
                catch (ArgumentException ex) { result = new("Error", Message: ex.Message); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { result = new("Error", Message: "无法访问该目录，请检查挂载状态。"); }
                catch (Exception) { result = Unavailable(); }
                finally { Interlocked.Exchange(ref active, 0); }
                completion.SetResult(result);
            }) { IsBackground = true, Name = "ScopedImport Windows folder picker" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return completion.Task;
        }
        catch (Exception)
        {
            Interlocked.Exchange(ref active, 0);
            return Task.FromResult(Unavailable());
        }
    }

    private static Result Unavailable() => new("Error", Message: "无法打开 Windows 文件夹选择窗口。请确认 Jellyfin 可访问当前交互桌面，或手动粘贴路径。");

    private static string TryPreferred(string root, string? preferred, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(preferred)) return root;
        try { return ValidateSelection(root, preferred, token); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return root; }
    }

    private static string ValidateSelection(string root, string path, CancellationToken token)
    {
        var target = Normalize(path);
        if (target is null || !Within(target, root)) throw new ArgumentException("请选择所选媒体库范围内的文件夹。");
        target = root + target[root.Length..];
        CheckDirectory(target, token);
        return target;
    }

    private static void CheckDirectory(string path, CancellationToken token)
    {
        var ancestry = new Stack<string>();
        for (var part = path; part is not null; part = Path.GetDirectoryName(part)) ancestry.Push(part);
        foreach (var part in ancestry)
        {
            token.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(part);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("请选择不含链接或重解析点的目录。");
            if ((attributes & FileAttributes.Directory) == 0) throw new ArgumentException("请选择文件夹。");
        }
    }

    private static bool Within(string path, string root) => path.Equals(root, Comparison)
        || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, Comparison);

    private static string? Normalize(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Length < 3 || path[1] != ':' ||
            !char.IsAsciiLetter(path[0]) || path.AsSpan(2).IndexOfAny(':', '*', '?') >= 0 ||
            path.Any(char.IsControl) || path.IndexOfAny(['<', '>', '|', '"']) >= 0) return null;
        if (path[3..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Any(part =>
            part.EndsWith(' ') || part.EndsWith('.') ||
            Regex.IsMatch(part, @"^(?:con|conin\$|conout\$|prn|aux|nul|com[0-9¹²³]|lpt[0-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))) return null;
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
