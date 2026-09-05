namespace AgentHarness.Llm;

/// <summary>
/// LLM 流里我们真正关心的几类事件。
///
/// OpenAI Responses 的 SSE 事件名很多（response.created、output_item.added、
/// content_part.added……），绝大多数只是生命周期噪音。
/// 客户端（<see cref="ResponsesSseMapper"/>）把它们收成下面这几个，
/// 于是 <c>AgentLoop</c> 完全不用出现 "response.output_text.delta" 这种供应商字符串。
///
/// 以后要接别的 Responses 兼容服务，只改 Client / Mapper，Loop 不用动。
/// </summary>
public abstract record LlmStreamEvent;

/// <summary>一段可见文本。UI 直接 Console.Write，就能做出打字机效果。</summary>
public sealed record TextDeltaEvent(string Delta) : LlmStreamEvent;

/// <summary>
/// 模型开始一个 function_call。此时 arguments 往往还是 ""，
/// 完整 JSON 要等后面的 <see cref="FunctionCallArgumentsDeltaEvent"/> 拼起来，
/// 或直接等 <see cref="ResponseCompletedEvent"/> 里的快照。
/// </summary>
public sealed record FunctionCallStartedEvent(string CallId, string Name) : LlmStreamEvent;

/// <summary>
/// arguments 的碎片，例如先到 <c>{"pa</c> 再到 <c>th":"a.txt"}</c>。
/// Loop 只转发给 UI；真正执行工具时用 completed 里已经拼好的字符串。
/// </summary>
public sealed record FunctionCallArgumentsDeltaEvent(string CallId, string Delta) : LlmStreamEvent;

/// <summary>
/// 这一轮 HTTP 流的官方结束事件。Text / FunctionCalls 都是完整快照。
/// Loop 只根据 FunctionCalls.Count 决定「停」还是「跑工具再问」。
/// </summary>
public sealed record ResponseCompletedEvent(
    string ResponseId,
    string Text,
    IReadOnlyList<FunctionCallItem> FunctionCalls) : LlmStreamEvent;

/// <summary>response.failed，或 HTTP / JSON 本身坏了。</summary>
public sealed record ResponseFailedEvent(string Message) : LlmStreamEvent;

/// <summary>
/// 一次 LLM 调用要带的东西。
/// Instructions 是系统提示；Input 是到目前为止的整段对话；
/// tools 不放在这里，由 Client 问 ToolRegistry 现取（工具集合很少变）。
/// </summary>
public sealed record LlmRequest(
    string Model,
    string Instructions,
    IReadOnlyList<InputItem> Input);

/// <summary>
/// 唯一需要实现的 LLM 接口：给一份请求，流出一串事件，最后应有 ResponseCompleted 或 ResponseFailed。
/// 本项目里由 <see cref="OpenAiResponsesClient"/> 实现。
/// </summary>
public interface ILlmClient
{
    IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request, CancellationToken cancellationToken);
}
