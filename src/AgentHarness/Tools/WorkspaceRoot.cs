namespace AgentHarness.Tools;

/// <summary>
/// 所有文件工具和 bash 的工作目录。
/// 模型传来的 path 都经过 <see cref="Resolve"/>：相对路径拼到 Root 下，
/// 绝对路径也必须落在 Root 里面，否则拒绝（挡住 <c>../</c> 逃逸）。
///
/// 这只是路径约束，不是安全沙箱。bash 仍能访问 Root 以外的东西（比如 <c>cat ~/.ssh/...</c>）。
/// </summary>
public sealed class WorkspaceRoot
{
    public WorkspaceRoot(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>把模型给的路径变成绝对路径；跳出 Root 则抛异常。</summary>
    public string Resolve(string userPath)
    {
        if (string.IsNullOrWhiteSpace(userPath))
            throw new ArgumentException("path 为空。");

        var combined = Path.IsPathRooted(userPath)
            ? Path.GetFullPath(userPath)
            : Path.GetFullPath(Path.Combine(Root, userPath));

        if (!IsInside(combined))
            throw new InvalidOperationException($"路径跳出了 workspace: {userPath}");

        return combined;
    }

    public bool IsInside(string fullPath)
    {
        var normalized = Path.GetFullPath(fullPath);

        // 比较时给 Root 补上目录分隔符，避免 /tmp/work 误匹配 /tmp/work-evil
        var prefix = Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        return normalized.Equals(Root, StringComparison.Ordinal)
               || normalized.StartsWith(prefix, StringComparison.Ordinal);
    }
}
