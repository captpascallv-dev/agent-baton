// SPDX-License-Identifier: MPL-2.0
namespace CliWatch.Collection;

public static class WatchReadBoundary
{
    static readonly AsyncLocal<Func<string,string>?> Guard = new();
    public static IDisposable Use(Func<string,string> check)
    {
        var old = Guard.Value; Guard.Value = check; return new Scope(() => Guard.Value = old);
    }
    sealed class Scope(Action restore) : IDisposable { public void Dispose() => restore(); }
    public static string Check(string path)
    {
#if WATCH_MAC
        return (Guard.Value ?? throw new IOException("Explicit fixture boundary required."))(path);
#else
        return path;
#endif
    }
}
#if WATCH_MAC
// These local names cover collector IO, including pointers found inside admitted
// metadata. Check immediately before access, not only at config validation.
internal static class File
{
    public static bool Exists(string? p) => p is not null && System.IO.File.Exists(WatchReadBoundary.Check(p));
    public static FileAttributes GetAttributes(string p) => System.IO.File.GetAttributes(WatchReadBoundary.Check(p));
    public static string ReadAllText(string p) => System.IO.File.ReadAllText(WatchReadBoundary.Check(p));
    public static Task<string> ReadAllTextAsync(string p, CancellationToken c) => System.IO.File.ReadAllTextAsync(WatchReadBoundary.Check(p), c);
    public static void WriteAllText(string p, string s) => throw new IOException("Mac collector is read-only.");
    public static void Move(string a, string b, bool overwrite) => throw new IOException("Mac collector is read-only.");
}
internal static class Directory
{
    public static bool Exists(string? p) => p is not null && System.IO.Directory.Exists(WatchReadBoundary.Check(p));
    public static string[] GetFiles(string p, string pattern = "*") => System.IO.Directory.GetFiles(WatchReadBoundary.Check(p), pattern);
    public static string[] GetDirectories(string p, string pattern = "*", SearchOption option = SearchOption.TopDirectoryOnly) => System.IO.Directory.GetDirectories(WatchReadBoundary.Check(p), pattern, option);
    public static IEnumerable<string> EnumerateFiles(string p, string pattern = "*") => GetFiles(p, pattern);
    public static IEnumerable<string> EnumerateDirectories(string p) => GetDirectories(p);
    public static DirectoryInfo CreateDirectory(string p) => throw new IOException("Mac collector is read-only.");
}
#endif
