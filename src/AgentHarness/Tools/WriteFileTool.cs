using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentHarness.Tools;

/// <summary>
/// 整文件覆盖写入。中间目录不存在就建出来。
/// 和 <see cref="EditFileTool"/> 的分工：新建 / 大改用 write，局部改用 edit。
/// </summary>
public sealed class WriteFileTool(WorkspaceRoot workspace) : ITool
{
    public string Name => "write_file";

    public string Description => "Create or overwrite a UTF-8 text file in the workspace.";

    public JsonObject Parameters => new()
    {
        ["path"] = new JsonObject
        {
            ["type"] = "string",
            ["description"] = "Relative path"
        },
        ["contents"] = new JsonObject
        {
            ["type"] = "string",
            ["description"] = "Full file contents"
        }
    };

    public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var path = arguments.GetProperty("path").GetString()
                   ?? throw new ArgumentException("missing path");
        var contents = arguments.GetProperty("contents").GetString() ?? "";
        var full = workspace.Resolve(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(full, contents, cancellationToken).ConfigureAwait(false);
        return $"wrote {contents.Length} chars to {path}";
    }
}
