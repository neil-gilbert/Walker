using System.Text;
using System.Text.RegularExpressions;
using Walker.Core;
namespace Walker.Git;
public sealed partial class GitChangeProvider(IProcessRunner runner, IProductionSourceScope? sourceScope = null) : IChangeProvider
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public async Task<IReadOnlyList<SourceChange>> GetChangesAsync(VerificationRequest request, CancellationToken cancellationToken)
    {
        async Task<string> Git(params string[] args)
        {
            // Unescaped UTF-8 paths keep diff headers parseable regardless of user configuration.
            var result = await runner.RunAsync(new("git", ["-c", "core.quotePath=false", .. args], request.Root, OutputLimit: 16 * 1024 * 1024), cancellationToken);
            if (result.OutputTruncated) throw new VerificationException("git_discovery_failed", "discovery", "Git output exceeds the safety limit; narrow the change.");
            if (result.ExitCode != 0) throw new VerificationException("git_discovery_failed", "discovery", "Git discovery failed: " + result.StandardError.Trim());
            return result.StandardOutput;
        }
        // Three-dot semantics use the merge base; compare that commit to the actual working copy.
        var baseCommit = (await Git("rev-parse", "--verify", "--end-of-options", request.Base + "^{commit}")).Trim();
        var mergeBase = (await Git("merge-base", baseCommit, "HEAD")).Trim();
        // One diff for every file: a single process, and rename detection sees both paths
        // (a per-file pathspec reports a renamed file as entirely new).
        var diff = await Git("diff", "--no-ext-diff", "--no-textconv", "--no-color", "--find-renames", "--src-prefix=a/", "--dst-prefix=b/",
            "--unified=0", "--diff-filter=AMR", mergeBase, "--", "*.cs");
        var excludes = (request.Exclude ?? []).Select(pattern => new Regex("^" + Regex.Escape(pattern.Replace('\\', '/'))
            .Replace(@"\*\*/", "(?:.*/)?").Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]") + "$",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)).ToArray();
        var changed = ParseDiff(diff);
        var names = changed.Keys.Where(file => changed[file].Count > 0 && !Ignore(file, excludes)).Order(StringComparer.Ordinal).ToArray();
        if (names.Length == 0) return [];
        // MSBuild evaluation is the slowest step; run it while Git lists dirty files.
        var dirtyTask = Git("diff", "--name-only", "-z", "HEAD", "--", "*.cs");
        var scopeTask = sourceScope?.GetFilesAsync(request, cancellationToken);
        await Task.WhenAll(scopeTask ?? Task.CompletedTask, dirtyTask);
        var dirty = dirtyTask.Result.Split('\0').ToHashSet(StringComparer.Ordinal);
        var compiledFiles = scopeTask?.Result;
        var testDirectories = new Dictionary<string, bool>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var result = new List<SourceChange>();
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(request.Project, request.Root))!;
        foreach (var file in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(file, request.Root);
            if (compiledFiles != null ? !compiledFiles.Contains(fullPath) : !fullPath.StartsWith(projectDirectory + Path.DirectorySeparatorChar, PathComparison)) continue;
            if (IsTestSource(Path.GetDirectoryName(fullPath)!, request.Root, testDirectories)) continue;
            if (await HasGeneratedHeaderAsync(fullPath, cancellationToken)) continue;
            result.Add(new(file, changed[file], dirty.Contains(file)));
        }
        return result;
    }
    // Maps new-side paths to changed line ranges. Headers are recognised only between `diff --git`
    // and the first hunk, so added content that happens to start with "+++ " is never a header.
    private static Dictionary<string, List<LineRange>> ParseDiff(string diff)
    {
        var files = new Dictionary<string, List<LineRange>>(StringComparer.Ordinal);
        List<LineRange>? current = null;
        var header = false;
        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal)) { header = true; current = null; continue; }
            if (header && line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var path = Unquote(line[4..].TrimEnd('\r'));
                current = path.StartsWith("b/", StringComparison.Ordinal) ? files[path[2..].Replace('\\', '/')] = [] : null;
                continue;
            }
            if (current == null || !line.StartsWith("@@ ", StringComparison.Ordinal)) continue;
            header = false;
            var match = Hunk().Match(line);
            if (!match.Success) continue;
            var start = int.Parse(match.Groups[1].Value);
            var count = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 1;
            // Deleted lines do not authorize mutating unchanged neighbours.
            if (count > 0) current.Add(new(start, start + count - 1));
        }
        return files;
    }
    // Git C-quotes unusual paths ("a\"b", octal UTF-8 bytes) and appends a tab to names containing spaces.
    private static string Unquote(string path)
    {
        if (!path.StartsWith('"')) return path.TrimEnd('\t');
        var end = path.LastIndexOf('"');
        var bytes = new List<byte>();
        for (var i = 1; i < end; i++)
        {
            if (path[i] != '\\') { bytes.AddRange(Encoding.UTF8.GetBytes(path[i].ToString())); continue; }
            var next = path[++i];
            if (next is >= '0' and <= '7') { bytes.Add(Convert.ToByte(path.Substring(i, 3), 8)); i += 2; continue; }
            bytes.Add(next switch { 'a' => 7, 'b' => 8, 't' => 9, 'n' => 10, 'v' => 11, 'f' => 12, 'r' => 13, _ => (byte)next });
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
    // Generated-code markers belong in the leading comment header; read only that far.
    private static async Task<bool> HasGeneratedHeaderAsync(string path, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith('#')) continue;
            if (!(text.StartsWith("//", StringComparison.Ordinal) || text.StartsWith("/*", StringComparison.Ordinal) || text.StartsWith('*'))) return false;
            if (text.Contains("<auto-generated", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
    private static bool IsTestSource(string directory, string root, Dictionary<string, bool> cache)
    {
        if (cache.TryGetValue(directory, out var result)) return result;
        var projects = Directory.GetFiles(directory, "*.csproj");
        if (projects.Length > 0)
        {
            result = projects.Any(project =>
            {
                var text = File.ReadAllText(project);
                return text.Contains("<IsTestProject>true", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase);
            });
        }
        else if (!string.Equals(directory, root, PathComparison) && Directory.GetParent(directory) is { } parent && parent.FullName.StartsWith(root, PathComparison))
            result = IsTestSource(parent.FullName, root, cache);
        cache[directory] = result;
        return result;
    }
    private static bool Ignore(string file, IReadOnlyList<Regex> excludes)
    {
        var segments = file.Split('/');
        if (segments.Any(s => s.Equals("bin", StringComparison.OrdinalIgnoreCase) || s.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Generated", StringComparison.OrdinalIgnoreCase) || s.Equals("tests", StringComparison.OrdinalIgnoreCase))) return true;
        if (file.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)) return true;
        return excludes.Any(pattern => pattern.IsMatch(file));
    }
    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex Hunk();
}
