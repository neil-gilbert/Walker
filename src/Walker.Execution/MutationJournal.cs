using System.Text.Json;
using Walker.Core;
namespace Walker.Execution;
// Crash-safe restoration record. It is written before a mutation is applied and removed after the
// original bytes are restored, so a killed process (SIGKILL, power loss) can be repaired on the next run.
public static class MutationJournal
{
    public static string PathFor(string root)
    {
        var git = Path.Combine(root, ".git");
        return Directory.Exists(git) ? Path.Combine(git, "walker-restore.json")
            : Path.Combine(Path.GetTempPath(), "walker-restore-" + Mutant.Hash(Path.GetFullPath(root))[..16] + ".json");
    }
    internal static async Task WriteAsync(string root, string path, byte[] original, byte[] mutated)
    {
        var journal = PathFor(root);
        var temporary = journal + ".tmp";
        var entry = new Entry(path, Convert.ToBase64String(original), Hash(mutated));
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            await JsonSerializer.SerializeAsync(stream, entry);
        File.Move(temporary, journal, overwrite: true);
    }
    internal static void Clear(string root) => File.Delete(PathFor(root));
    // Returns a message when a file was restored. Refuses to overwrite edits made after the interruption.
    public static async Task<string?> RecoverAsync(string root, CancellationToken cancellationToken)
    {
        var journal = PathFor(root);
        if (!File.Exists(journal)) return null;
        Entry? entry;
        try { entry = JsonSerializer.Deserialize<Entry>(await File.ReadAllTextAsync(journal, cancellationToken)); }
        catch (JsonException) { entry = null; }
        if (entry == null) throw new InvalidOperationException("Unreadable Walker restore journal; inspect and delete it: " + journal);
        var original = Convert.FromBase64String(entry.Original);
        if (!File.Exists(entry.Path)) throw new InvalidOperationException($"An interrupted Walker run mutated {entry.Path}, which no longer exists. Inspect it, then delete {journal}.");
        var current = await File.ReadAllBytesAsync(entry.Path, cancellationToken);
        if (current.AsSpan().SequenceEqual(original)) { File.Delete(journal); return null; }
        if (Hash(current) != entry.MutatedHash)
            throw new InvalidOperationException($"An interrupted Walker run left {entry.Path} mutated, and it has changed since. Inspect it, then delete {journal}.");
        await File.WriteAllBytesAsync(entry.Path, original, CancellationToken.None);
        File.Delete(journal);
        return $"Restored {entry.Path} after an interrupted Walker run.";
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    private sealed record Entry(string Path, string Original, string MutatedHash);
}
