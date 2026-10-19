namespace ForkBridge.Core.Infrastructure;

/// <summary>All deletion performed by ForkBridge goes through this class. It only deletes inside the
/// harness state root, requires an ownership marker, never follows symlinks/junctions, and rejects
/// path traversal.</summary>
public sealed class SafeFileSystem
{
    public const string OwnershipMarker = ".forkbridge-owned";
    private readonly string _root;

    public SafeFileSystem(string harnessRoot)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(harnessRoot));
    }

    public string Root => _root;

    public bool IsInsideRoot(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.StartsWith(_root + Path.DirectorySeparatorChar, cmp);
    }

    /// <summary>Throws if any existing component between the root and <paramref name="path"/> is a
    /// symlink or junction (reparse point). This prevents a planted link from redirecting a
    /// harness operation into a user repository.</summary>
    public void AssertNoLinksBetweenRootAndPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!IsInsideRoot(full)) throw new HarnessSafetyException($"Path '{full}' is outside the harness root '{_root}'.");
        var rel = Path.GetRelativePath(_root, full);
        var cur = _root;
        foreach (var part in rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "..") throw new HarnessSafetyException($"Path traversal in '{path}'.");
            cur = Path.Combine(cur, part);
            var info = new FileInfo(cur);
            if (!info.Exists && !Directory.Exists(cur)) break;
            if (IsLink(cur)) throw new HarnessSafetyException($"'{cur}' is a symbolic link or junction; refusing to operate through it.");
        }
    }

    public static bool IsLink(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if (attrs.HasFlag(FileAttributes.ReparsePoint)) return true;
            var fsi = Directory.Exists(path) ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
            return fsi.LinkTarget is not null;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    public string CreateOwnedDirectory(string path, string ownerId)
    {
        AssertNoLinksBetweenRootAndPath(path);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, OwnershipMarker), ownerId);
        return path;
    }

    public string? ReadOwner(string dir)
    {
        var marker = Path.Combine(dir, OwnershipMarker);
        return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
    }

    /// <summary>Deletes a harness-owned directory. Links inside it are removed as links (their
    /// targets are never touched).</summary>
    public void DeleteOwnedDirectory(string dir, string expectedOwner)
    {
        var full = Path.GetFullPath(dir);
        AssertNoLinksBetweenRootAndPath(full);
        if (!Directory.Exists(full)) return;
        var owner = ReadOwner(full);
        if (owner != expectedOwner)
            throw new HarnessSafetyException($"Refusing to delete '{full}': ownership marker is '{owner ?? "<missing>"}', expected '{expectedOwner}'.");
        DeleteTreeNoFollow(full);
    }

    /// <summary>Deletes a sub-directory of a harness-owned directory (ownership checked on the parent).</summary>
    public void DeleteInsideOwned(string path, string ownedDir, string expectedOwner)
    {
        var full = Path.GetFullPath(path);
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ownedDir));
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(parent + Path.DirectorySeparatorChar, cmp)) throw new HarnessSafetyException($"'{full}' is not inside '{parent}'.");
        if (ReadOwner(parent) != expectedOwner) throw new HarnessSafetyException($"'{parent}' is not owned by '{expectedOwner}'.");
        AssertNoLinksBetweenRootAndPath(full);
        if (Directory.Exists(full)) DeleteTreeNoFollow(full);
    }

    private void DeleteTreeNoFollow(string dir)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
        {
            if (!IsInsideRoot(entry)) throw new HarnessSafetyException($"Entry '{entry}' escapes harness root.");
            if (IsLink(entry))
            {
                // Remove the link itself, never its target.
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: false);
                else File.Delete(entry);
                continue;
            }
            if (Directory.Exists(entry)) DeleteTreeNoFollow(entry);
            else
            {
                File.SetAttributes(entry, FileAttributes.Normal); // git objects are read-only on Windows
                File.Delete(entry);
            }
        }
        Directory.Delete(dir, recursive: false);
    }
}

public sealed class HarnessSafetyException(string message) : Exception(message);
