using ForkBridge.Core.Storage;
using ForkBridge.Merge.Git;
using ForkBridge.Merge.Model;

namespace ForkBridge.Merge.Planning;

/// <summary>Maps Git's unmerged index stages and conflict messages to a conflict kind and to what can be done
/// with it here. Conflict kind (what Git reported), capability (what this tool can do) and state (resolved or not)
/// are separate on purpose: "can be edited here" is never "fixed automatically".</summary>
public static class EntryClassifier
{
    public static string EntryId(string path) => "e" + StateStore.Sha256(path)[..16];

    /// <summary>Repository-relative path accepted for workspace operations: relative, '/'-separated, no '..',
    /// no '.git' component, no control characters or backslashes, valid UTF-8.</summary>
    public static string? PathProblem(string path)
    {
        if (string.IsNullOrEmpty(path)) return "empty path";
        if (path.Contains('�')) return "path is not valid UTF-8";
        if (path.StartsWith('/') || path.Contains('\\') || (path.Length > 1 && path[1] == ':')) return "absolute or non-portable path";
        if (path.Any(c => c < 0x20 || c == 0x7F)) return "control character in path";
        foreach (var seg in path.Split('/'))
        {
            if (seg is "" or "." or "..") return "path traversal segment";
            if (seg.Equals(".git", StringComparison.OrdinalIgnoreCase)) return "'.git' path component";
        }
        return null;
    }

    private static readonly Dictionary<string, ConflictKind> MessageKinds = new(StringComparer.Ordinal)
    {
        ["CONFLICT (rename/rename)"] = ConflictKind.RenameRename,
        ["CONFLICT (rename involved in collision)"] = ConflictKind.RenameRename,
        ["CONFLICT (rename/delete)"] = ConflictKind.RenameDelete,
        ["CONFLICT (file/directory)"] = ConflictKind.FileDirectory,
        ["CONFLICT (directory rename split)"] = ConflictKind.RenameRename,
        ["CONFLICT (implicit dir rename)"] = ConflictKind.RenameRename,
        ["CONFLICT (distinct modes)"] = ConflictKind.DistinctTypes,
        ["CONFLICT (submodule lacks merge base)"] = ConflictKind.Submodule,
        ["CONFLICT (submodule not checked out)"] = ConflictKind.Submodule,
        ["CONFLICT (submodule history not available)"] = ConflictKind.Submodule,
        ["CONFLICT (submodule may have rewound)"] = ConflictKind.Submodule,
        ["CONFLICT (submodule)"] = ConflictKind.Submodule,
    };

    public static (ConflictKind Kind, bool Binary) Kind(IReadOnlyCollection<StageEntry> stages, IReadOnlyCollection<MergeMessage> messages)
    {
        bool binary = messages.Any(m => m.Type == "CONFLICT (binary)");
        foreach (var m in messages)
            if (MessageKinds.TryGetValue(m.Type, out var k)) return (k, binary);
        if (stages.Any(s => s.Mode == "160000")) return (ConflictKind.Submodule, binary);
        if (stages.Any(s => s.Mode == "120000")) return (ConflictKind.Symlink, binary);
        var modes = stages.Select(s => s.Mode).Distinct().ToList();
        if (modes.Any(m => m is not ("100644" or "100755"))) return (ConflictKind.DistinctTypes, binary);
        bool b = stages.Any(s => s.Stage == 1), t = stages.Any(s => s.Stage == 2), src = stages.Any(s => s.Stage == 3);
        if (t && src) return (b ? (binary ? ConflictKind.Binary : ConflictKind.Content) : (binary ? ConflictKind.Binary : ConflictKind.AddAdd), binary);
        if (b && t && !src) return (ConflictKind.DeletedInSource, binary);
        if (b && src && !t) return (ConflictKind.DeletedInTarget, binary);
        return (ConflictKind.Other, binary);
    }

    public static EntryCapabilities Capabilities(ConflictKind kind, bool binary, string path)
    {
        var problem = PathProblem(path);
        if (problem is not null)
            return Blocked(Restriction.UnsafePath, $"Unsafe path ({problem}); this merge cannot be completed here.");
        return kind switch
        {
            ConflictKind.Content or ConflictKind.AddAdd when !binary => new EntryCapabilities { Supported = true, EditText = true, TakeTarget = true, TakeSource = true },
            ConflictKind.Content or ConflictKind.AddAdd or ConflictKind.Binary => new EntryCapabilities { Supported = true, TakeTarget = true, TakeSource = true,
                Reason = "Binary content: choose the target or the source version." },
            ConflictKind.DeletedInSource => new EntryCapabilities { Supported = true, KeepModified = true, Delete = true,
                Reason = "The source branch deleted this file and the target branch modified it: keep the modified file or delete it." },
            ConflictKind.DeletedInTarget => new EntryCapabilities { Supported = true, KeepModified = true, Delete = true,
                Reason = "The target branch deleted this file and the source branch modified it: keep the modified file or delete it." },
            ConflictKind.RenameRename => Blocked(Restriction.StructuralConflict, "Rename conflict (both sides renamed, or a rename collides with another path). Not supported here: resolve it in a local clone."),
            ConflictKind.RenameDelete => Blocked(Restriction.StructuralConflict, "One side renamed the file, the other deleted it. Not supported here: resolve it in a local clone."),
            ConflictKind.FileDirectory => Blocked(Restriction.StructuralConflict, "A file on one side collides with a directory on the other. Not supported here."),
            ConflictKind.Symlink => Blocked(Restriction.Symlink, "Symbolic-link conflict. Not supported here."),
            ConflictKind.Submodule => Blocked(Restriction.Submodule, "Submodule conflict. Not supported here."),
            ConflictKind.DistinctTypes => Blocked(Restriction.StructuralConflict, "The sides disagree on the file type or mode. Not supported here."),
            _ => Blocked(Restriction.StructuralConflict, "Git reported a conflict this tool does not model. Not supported here."),
        };
    }

    public static EntryCapabilities Blocked(Restriction r, string reason) => new() { Supported = false, Restriction = r, Reason = reason };

    public static readonly EntryCapabilities CleanEntry = new() { Supported = true, Reason = "Merged by Git without conflict." };
}
