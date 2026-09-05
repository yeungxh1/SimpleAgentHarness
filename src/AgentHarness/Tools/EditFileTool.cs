using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentHarness.Tools;

/// <summary>
/// 最小的补丁工具：整段 <c>old_text</c> 替换成 <c>new_text</c>。
/// 出现 0 次或超过 1 次都失败 —— 逼模型给出「只出现一次」的片段，避免改错地方。
/// 真编辑器会用 diff / 行号，这里故意保持能一眼看完。
/// </summary>
public sealed class EditFileTool(WorkspaceRoot workspace) : ITool
{
    public string Name => "edit_file";

    public string Description =>
        "Replace exactly one occurrence of old_text with new_text in a workspace file.";

    public JsonObject Parameters => new()
    {
        ["path"] = new JsonObject { ["type"] = "string" },
        ["old_text"] = new JsonObject { ["type"] = "string" },
        ["new_text"] = new JsonObject { ["type"] = "string" }
    };

    public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var path = arguments.GetProperty("path").GetString()
                   ?? throw new ArgumentException("missing path");
        var oldText = arguments.GetProperty("old_text").GetString()
                      ?? throw new ArgumentException("missing old_text");
        var newText = arguments.GetProperty("new_text").GetString() ?? "";

        if (oldText.Length == 0)
            return "ERROR: old_text is empty";

        var full = workspace.Resolve(path);
        if (!File.Exists(full))
            return $"ERROR: file not found: {path}";

        var original = await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false);
        var first = original.IndexOf(oldText, StringComparison.Ordinal);
        if (first < 0)
            return "ERROR: old_text not found";

        var second = original.IndexOf(oldText, first + oldText.Length, StringComparison.Ordinal);
        if (second >= 0)
            return "ERROR: old_text matches more than once; make the snippet unique";

        var updated = string.Concat(original.AsSpan(0, first), newText, original.AsSpan(first + oldText.Length));
        await File.WriteAllTextAsync(full, updated, cancellationToken).ConfigureAwait(false);
        return $"edited {path}";
    }
}
