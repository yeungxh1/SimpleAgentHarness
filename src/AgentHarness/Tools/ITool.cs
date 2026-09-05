using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentHarness.Tools;

/// <summary>
/// 一个本地工具。同一份信息要服务两个读者：
/// <list type="bullet">
/// <item>模型：靠 Name / Description / Parameters（JSON Schema）决定「要不要点我、带什么参数」</item>
/// <item>运行时：靠 ExecuteAsync 真正干活</item>
/// </list>
/// 返回值永远是字符串。失败也写成 <c>ERROR: ...</c>，让模型能读到并改参数重试。
/// </summary>
public interface ITool
{
    string Name { get; }
    string Description { get; }

    /// <summary>
    /// JSON Schema 的 <c>properties</c> 对象。
    /// <see cref="ToolRegistry"/> 会把它包进 Responses API 的 <c>tools[].parameters</c>。
    /// </summary>
    JsonObject Parameters { get; }

    Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken);
}
