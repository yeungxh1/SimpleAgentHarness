using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentHarness.Runtime;

namespace AgentHarness;

/// <summary>POST /turns 的请求体。一句话 = 一次用户回合（里面可能有多次 LLM HTTP）。</summary>
public sealed record TurnRequest(string? Message);

/// <summary>GET /info 的响应。不含 ApiKey。</summary>
public sealed record HarnessInfo(string Workspace, string Model, string BaseUrl);

/// <summary>
/// 进程里只有一份 Runtime。Gate 保证同一时刻只跑一个回合（conversation 不是线程安全的）。
/// </summary>
public sealed class AgentHost
{
    public required AgentRuntime Runtime { get; init; }
    public required string WorkspaceRoot { get; init; }
    public required string Model { get; init; }
    public required string BaseUrl { get; init; }
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public static readonly JsonSerializerOptions EventJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };
}
