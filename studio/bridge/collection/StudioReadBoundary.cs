// SPDX-License-Identifier: MPL-2.0
namespace CliWatch.Collection;

public static class StudioReadBoundary
{
    public static string? AllowedRoot { get; set; }
    public static string Check(string path)
    {
#if STUDIO_MAC
        if (AllowedRoot is null) throw new IOException("Explicit fixture boundary required.");
        return CliWatch.Mac.FixtureBoundary.CheckedPath(path, AllowedRoot);
#else
        return path;
#endif
    }
}
#if STUDIO_MAC
// These local names cover collector IO, including pointers found inside admitted
// metadata. Check immediately before access, not only at config validation.
internal static class File
{
    public static bool Exists(string? p) => p is not null && System.IO.File.Exists(StudioReadBoundary.Check(p));
    public static FileAttributes GetAttributes(string p) => System.IO.File.GetAttributes(StudioReadBoundary.Check(p));
    public static string ReadAllText(string p) => System.IO.File.ReadAllText(StudioReadBoundary.Check(p));
    public static Task<string> ReadAllTextAsync(string p, CancellationToken c) => System.IO.File.ReadAllTextAsync(StudioReadBoundary.Check(p), c);
    public static void WriteAllText(string p, string s) => throw new IOException("Mac collector is read-only.");
    public static void Move(string a, string b, bool overwrite) => throw new IOException("Mac collector is read-only.");
}
internal static class Directory
{
    public static bool Exists(string? p) => p is not null && System.IO.Directory.Exists(StudioReadBoundary.Check(p));
    public static string[] GetFiles(string p, string pattern = "*") => System.IO.Directory.GetFiles(StudioReadBoundary.Check(p), pattern);
    public static string[] GetDirectories(string p, string pattern = "*", SearchOption option = SearchOption.TopDirectoryOnly)
    {
        if (option != SearchOption.TopDirectoryOnly) throw new IOException("Recursive traversal requires per-directory validation.");
        return System.IO.Directory.GetDirectories(StudioReadBoundary.Check(p), pattern, option);
    }
    public static IEnumerable<string> EnumerateFiles(string p, string pattern = "*") => GetFiles(p, pattern);
    public static IEnumerable<string> EnumerateDirectories(string p) => GetDirectories(p);
    public static DirectoryInfo CreateDirectory(string p) => throw new IOException("Mac collector is read-only.");
}
#endif
