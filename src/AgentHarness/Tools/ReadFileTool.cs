using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentHarness.Tools;

/// <summary>
/// 读 workspace 里的文本文件。超过 <see cref="MaxBytes"/> 会截断并注明原大小，
/// 避免把几 MB 日志一次性塞进下一轮 LLM 请求。
/// </summary>
public sealed class ReadFileTool(WorkspaceRoot workspace) : ITool
{
    public const int MaxBytes = 64 * 1024;

    public string Name => "read_file";

    // Description / Parameters 是给模型看的英文：它比中文更常出现在训练数据里。
    public string Description => "Read a UTF-8 text file from the workspace. Path is relative to the workspace root.";

    public JsonObject Parameters => new()
    {
        ["path"] = new JsonObject
        {
            ["type"] = "string",
            ["description"] = "Relative path, e.g. hello.txt"
        }
    };

    public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var path = arguments.GetProperty("path").GetString()
                   ?? throw new ArgumentException("missing path");
        var full = workspace.Resolve(path);
        if (!File.Exists(full))
            return Task.FromResult($"ERROR: file not found: {path}");

        cancellationToken.ThrowIfCancellationRequested();
        var bytes = File.ReadAllBytes(full);
        var truncated = bytes.Length > MaxBytes;
        var text = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, MaxBytes));
        if (truncated)
            text += $"\n... truncated, file is {bytes.Length} bytes";

        return Task.FromResult(text);
    }
}
