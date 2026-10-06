namespace AgenticSdlc.Core.Workspace;

/// <summary>A single file operation proposed by an agent. Agents propose; the orchestrator applies after policy and approval.</summary>
public sealed class FileChange
{
    public required string Path { get; init; }
    /// <summary>create | modify | delete</summary>
    public required string Action { get; init; }
    public string? Content { get; init; }
    public string? Reason { get; init; }
}

public sealed class ChangeSet
{
    public required string Summary { get; init; }
    public List<FileChange> Files { get; init; } = new();
    public string Impact { get; init; } = "Low";
    public List<string> Risks { get; init; } = new();
    public List<string> Assumptions { get; init; } = new();
}

/// <summary>
/// The sandboxed directory that agents may modify. All writes are path-contained (no escaping the root),
/// and snapshots make every Implementation attempt reversible.
/// </summary>
public sealed class Workspace
{
    public Workspace(string rootPath, string snapshotsPath)
    {
        RootPath = Path.GetFullPath(rootPath);
        SnapshotsPath = Path.GetFullPath(snapshotsPath);
        Directory.CreateDirectory(RootPath);
        Directory.CreateDirectory(SnapshotsPath);
    }

    public string RootPath { get; }
    public string SnapshotsPath { get; }

    public string Resolve(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) throw new ArgumentException("Path is empty.");
        if (Path.IsPathRooted(relativePath)) throw new InvalidOperationException($"Absolute paths are not allowed: {relativePath}");
        var full = Path.GetFullPath(Path.Combine(RootPath, relativePath));
        var root = RootPath.EndsWith(Path.DirectorySeparatorChar) ? RootPath : RootPath + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Path escapes the workspace: {relativePath}");
        return full;
    }

    public bool Exists(string relativePath) => File.Exists(Resolve(relativePath));

    public string? ReadText(string relativePath)
    {
        var full = Resolve(relativePath);
        return File.Exists(full) ? File.ReadAllText(full) : null;
    }

    public IReadOnlyList<string> ListFiles(string? extensionFilter = null)
    {
        if (!Directory.Exists(RootPath)) return Array.Empty<string>();
        return Directory.EnumerateFiles(RootPath, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}TestResults{Path.DirectorySeparatorChar}"))
            .Where(f => extensionFilter is null || f.EndsWith(extensionFilter, StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(RootPath, f).Replace('\\', '/'))
            .OrderBy(f => f)
            .ToList();
    }

    /// <summary>Applies a change set. Returns the list of relative paths touched.</summary>
    public IReadOnlyList<string> Apply(ChangeSet changeSet)
    {
        var touched = new List<string>();
        foreach (var change in changeSet.Files)
        {
            var full = Resolve(change.Path);
            switch (change.Action.ToLowerInvariant())
            {
                case "create":
                case "modify":
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.WriteAllText(full, change.Content ?? string.Empty);
                    break;
                case "delete":
                    if (File.Exists(full)) File.Delete(full);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown file action '{change.Action}' for {change.Path}");
            }
            touched.Add(change.Path);
        }
        return touched;
    }

    /// <summary>Copies the workspace (excluding build output) into a snapshot folder and returns the snapshot id.</summary>
    public string Snapshot(string label)
    {
        var id = $"{DateTime.UtcNow:HHmmss-fff}-{label}";
        var target = Path.Combine(SnapshotsPath, id);
        CopyDirectory(RootPath, target);
        return id;
    }

    /// <summary>Restores the workspace to a snapshot: deletes current content then copies the snapshot back.</summary>
    public void Restore(string snapshotId)
    {
        var source = Path.Combine(SnapshotsPath, snapshotId);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"Snapshot {snapshotId} not found.");
        foreach (var entry in Directory.GetFileSystemEntries(RootPath))
        {
            if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
            else File.Delete(entry);
        }
        CopyDirectory(source, RootPath);
    }

    public static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(dir);
            if (name is "bin" or "obj" or "TestResults" or ".git") continue;
            var rel = Path.GetRelativePath(source, dir);
            if (rel.Split(Path.DirectorySeparatorChar).Any(seg => seg is "bin" or "obj" or "TestResults" or ".git")) continue;
            Directory.CreateDirectory(Path.Combine(target, rel));
        }
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            if (rel.Split(Path.DirectorySeparatorChar).Any(seg => seg is "bin" or "obj" or "TestResults" or ".git")) continue;
            var dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }
}
