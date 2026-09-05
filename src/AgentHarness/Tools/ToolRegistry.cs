using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentHarness.Tools;

/// <summary>
/// 名字 → 工具。同时负责两件「模型看得见 / 本地跑得着」的事：
/// <list type="number">
/// <item><see cref="ToResponsesTools"/>：生成请求体里的 tools 数组，模型据此决定调用谁</item>
/// <item><see cref="ExecuteAsync"/>：按模型传来的 name + arguments JSON 分派执行</item>
/// </list>
/// AgentLoop 只跟注册表说话，不直接 new 某个 Tool。
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools;

    public ToolRegistry(IEnumerable<ITool> tools)
        => _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

    /// <summary>
    /// Responses API 的 function 工具是扁平的：type/name/description/parameters
    /// （没有 Chat Completions 那种再包一层 function: { ... }）。
    /// </summary>
    public JsonArray ToResponsesTools()
    {
        var array = new JsonArray();
        foreach (var tool in _tools.Values)
        {
            array.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = tool.Parameters.DeepClone(),
                    ["required"] = new JsonArray(tool.Parameters.Select(p => JsonValue.Create(p.Key)).ToArray()),
                    ["additionalProperties"] = false
                }
            });
        }

        return array;
    }

    /// <summary>
    /// 任何失败都变成字符串返回，不往外抛。
    /// 这样 AgentLoop 总能写出一条 function_call_output，模型才有机会纠错。
    /// </summary>
    public async Task<string> ExecuteAsync(string name, string argumentsJson, CancellationToken cancellationToken)
    {
        if (!_tools.TryGetValue(name, out var tool))
            return $"ERROR: unknown tool '{name}'";

        JsonElement args;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            // Clone：doc dispose 之后 args 还要交给工具用
            args = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return $"ERROR: arguments 不是 JSON: {ex.Message}";
        }

        try
        {
            return await tool.ExecuteAsync(args, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }
    }
}
