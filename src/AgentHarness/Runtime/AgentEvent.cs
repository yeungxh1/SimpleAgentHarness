namespace AgentHarness.Runtime;

/// <summary>
/// Runtime 对外广播的事件种类。UI / 日志 / 测试都只盯着这一种类型。
///
/// 一次「读 hello.txt」在时间上大概是：
///   TurnStarted
///     LlmStarted(#1) → ToolStarted → ToolArgumentsDelta → LlmFinished
///     ToolFinished                         （本地执行，没有新的 HTTP）
///     LlmStarted(#2) → TextDelta × N → LlmFinished
///   TurnFinished
/// </summary>
public enum AgentEventKind
{
    /// <summary>用户刚说完一句话，即将进入 loop。</summary>
    TurnStarted,

    /// <summary>一次 LLM HTTP 开始。Turn 是这次用户发言里的第几次请求。</summary>
    LlmStarted,

    /// <summary>模型吐出一小段可见文本。</summary>
    TextDelta,

    /// <summary>模型点名了一个工具，arguments 多半还没到齐。</summary>
    ToolStarted,

    /// <summary>工具参数 JSON 的碎片。</summary>
    ToolArgumentsDelta,

    /// <summary>本地已经执行完，Output 是给模型看的字符串。</summary>
    ToolFinished,

    /// <summary>这一次 HTTP 的 SSE 流结束。</summary>
    LlmFinished,

    /// <summary>loop 结束，Text 是最终回复（也可能是 MaxTurns 的提示）。</summary>
    TurnFinished,

    /// <summary>HTTP 失败、JSON 坏了、或触及 MaxTurns。</summary>
    Error
}

/// <summary>
/// 扁平事件：所有字段堆在一个 record 上，用不到的保持 null。
/// 比「每种事件一个子类」啰嗦一点，但对照 Console 输出时更好认。
/// </summary>
public sealed record AgentEvent
{
    public required AgentEventKind Kind { get; init; }

    /// <summary>仅 LlmStarted / LlmFinished：这是本轮用户发言里的第几次 LLM 请求。</summary>
    public int? Turn { get; init; }

    /// <summary>用户原文、文本碎片、最终回复或错误信息，视 Kind 而定。</summary>
    public string? Text { get; init; }

    public string? ToolName { get; init; }

    /// <summary>Responses API 的 call_id，用来把 function_call 和 function_call_output 配成对。</summary>
    public string? ToolCallId { get; init; }

    public string? Arguments { get; init; }
    public string? Output { get; init; }

    public static AgentEvent TurnStarted(string userMessage) =>
        new() { Kind = AgentEventKind.TurnStarted, Text = userMessage };

    public static AgentEvent LlmStarted(int turn) =>
        new() { Kind = AgentEventKind.LlmStarted, Turn = turn };

    public static AgentEvent TextDelta(string delta) =>
        new() { Kind = AgentEventKind.TextDelta, Text = delta };

    public static AgentEvent ToolStarted(string callId, string name) =>
        new() { Kind = AgentEventKind.ToolStarted, ToolCallId = callId, ToolName = name };

    public static AgentEvent ToolArgumentsDelta(string callId, string delta) =>
        new() { Kind = AgentEventKind.ToolArgumentsDelta, ToolCallId = callId, Arguments = delta };

    public static AgentEvent ToolFinished(string callId, string name, string arguments, string output) =>
        new()
        {
            Kind = AgentEventKind.ToolFinished,
            ToolCallId = callId,
            ToolName = name,
            Arguments = arguments,
            Output = output
        };

    public static AgentEvent LlmFinished(int turn) =>
        new() { Kind = AgentEventKind.LlmFinished, Turn = turn };

    public static AgentEvent TurnFinished(string assistantText) =>
        new() { Kind = AgentEventKind.TurnFinished, Text = assistantText };

    public static AgentEvent Error(string message) =>
        new() { Kind = AgentEventKind.Error, Text = message };
}

/// <summary>
/// Loop 往外推事件用的回调。
/// Runtime 把它转成 C# event；单测可以直接收这个委托，不必挂 EventHandler。
/// </summary>
public delegate ValueTask AgentEventSink(AgentEvent evt, CancellationToken cancellationToken);
