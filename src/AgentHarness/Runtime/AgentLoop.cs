using AgentHarness.Llm;
using AgentHarness.Tools;

namespace AgentHarness.Runtime;

public sealed class AgentLoopOptions
{
    public string Model { get; init; } = "gpt-4.1";

    /// <summary>
    /// 系统提示，对应 Responses API 的 instructions 字段。
    /// 它不是 conversation 里的一条 message，每次请求都会单独带上。
    /// </summary>
    public string Instructions { get; init; } = """
        You are a small local coding agent. The working directory is a sandbox called the workspace.
        Use tools when you need file contents or to change files. Paths are relative to the workspace.
        Prefer short answers.
        """;

    /// <summary>
    /// 一次用户发言里，最多向模型发几次 HTTP。
    /// 模型如果一直 function_call，没有这个上限就会死循环（读完再读、改完再改）。
    /// </summary>
    public int MaxTurns { get; init; } = 8;
}

/// <summary>
/// Harness 的心脏。伪代码只有这几行：
/// <code>
/// 把用户那句话放进 conversation
/// while 还没到 MaxTurns:
///     把 conversation + tools 发给模型（SSE）
///     若模型只回了文本     → 结束，把文本交给用户
///     若模型回了 function_call → 本地执行，把结果追加进 conversation，再问一次
/// </code>
/// 「一轮用户发言」常常对应多次 HTTP。这就是 agent loop，不是普通的「一问一答」。
/// </summary>
public sealed class AgentLoop
{
    private readonly ILlmClient _llm;
    private readonly ToolRegistry _tools;
    private readonly AgentLoopOptions _options;

    public AgentLoop(ILlmClient llm, ToolRegistry tools, AgentLoopOptions? options = null)
    {
        _llm = llm;
        _tools = tools;
        _options = options ?? new AgentLoopOptions();
    }

    /// <param name="conversation">
    /// 可变列表，由 Runtime 持有。本方法会往末尾追加 function_call / function_call_output。
    /// 调用方必须已经把当前这句用户输入加进去。
    /// </param>
    /// <param name="onEvent">每发生一件「值得让 UI 知道的事」就回调一次。</param>
    public async Task<string> RunAsync(
        IList<InputItem> conversation,
        AgentEventSink onEvent,
        CancellationToken cancellationToken)
    {
        for (var turn = 1; turn <= _options.MaxTurns; turn++)
        {
            await onEvent(AgentEvent.LlmStarted(turn), cancellationToken).ConfigureAwait(false);

            // 一次 HTTP：把到目前为止的全部 input 发给模型，边收 SSE 边往外推事件。
            var completed = await StreamOneRequestAsync(conversation, onEvent, cancellationToken)
                .ConfigureAwait(false);

            await onEvent(AgentEvent.LlmFinished(turn), cancellationToken).ConfigureAwait(false);

            // 模型这轮没点名任何工具 = 它认为可以回答用户了，loop 结束。
            if (completed.FunctionCalls.Count == 0)
                return completed.Text;

            // Responses API 约定：下一轮请求里必须同时带上
            //   1) 模型自己刚才产出的 function_call（原样回传）
            //   2) 本地执行后的 function_call_output
            // 两者用同一个 call_id 拉上拉链，模型才知道「这个结果对应刚才那个调用」。
            foreach (var call in completed.FunctionCalls)
                conversation.Add(call);

            // 教学实现：多个工具按顺序跑。真 harness 里常常并行。
            foreach (var call in completed.FunctionCalls)
            {
                var output = await _tools.ExecuteAsync(call.Name, call.Arguments, cancellationToken)
                    .ConfigureAwait(false);
                await onEvent(
                    AgentEvent.ToolFinished(call.CallId, call.Name, call.Arguments, output),
                    cancellationToken).ConfigureAwait(false);
                conversation.Add(new FunctionCallOutputItem(call.CallId, output));
            }

            // 然后 for 继续：带着刚追加的 call + output 再 POST 一次。
        }

        var message = $"已达到 MaxTurns={_options.MaxTurns}，强制结束，避免工具循环。";
        await onEvent(AgentEvent.Error(message), cancellationToken).ConfigureAwait(false);
        return message;
    }

    /// <summary>
    /// 消费一整条 SSE 流。中间的 delta 只用来通知 UI；真正做决策看最后的 <see cref="ResponseCompletedEvent"/>。
    /// </summary>
    private async Task<ResponseCompletedEvent> StreamOneRequestAsync(
        IList<InputItem> conversation,
        AgentEventSink onEvent,
        CancellationToken cancellationToken)
    {
        // ToList：给 LLM 一份快照。本轮流式过程中我们不会改 conversation，
        // 工具结果是在 Stream 返回之后才追加的。
        var request = new LlmRequest(_options.Model, _options.Instructions, conversation.ToList());
        ResponseCompletedEvent? completed = null;

        await foreach (var ev in _llm.StreamAsync(request, cancellationToken).ConfigureAwait(false))
        {
            switch (ev)
            {
                case TextDeltaEvent text:
                    await onEvent(AgentEvent.TextDelta(text.Delta), cancellationToken).ConfigureAwait(false);
                    break;
                case FunctionCallStartedEvent started:
                    await onEvent(AgentEvent.ToolStarted(started.CallId, started.Name), cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case FunctionCallArgumentsDeltaEvent delta:
                    await onEvent(AgentEvent.ToolArgumentsDelta(delta.CallId, delta.Delta), cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case ResponseCompletedEvent done:
                    // 完整快照来了。arguments 已经拼好，loop 上面用它执行工具。
                    completed = done;
                    break;
                case ResponseFailedEvent failed:
                    await onEvent(AgentEvent.Error(failed.Message), cancellationToken).ConfigureAwait(false);
                    throw new InvalidOperationException(failed.Message);
            }
        }

        // 正常的 Responses 流最后一定有 response.completed。没有 = 连接被掐断或实现漏了。
        return completed
               ?? throw new InvalidOperationException("SSE 流结束，但没有 response.completed。");
    }
}
