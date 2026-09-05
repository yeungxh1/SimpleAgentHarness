using System.Text.Encodings.Web;
using System.Text.Json;
using AgentHarness.Llm;

namespace AgentHarness.Runtime;

/// <summary>
/// 比 Loop 高一层，像进程内的「agent 服务器」：
/// <list type="bullet">
/// <item>跨多次用户输入保存同一份 conversation（你第二句还能提到第一句读过的文件）</item>
/// <item>把 Loop 的回调转成普通 C# event，Console / 测试 / 以后的 UI 都能订阅</item>
/// </list>
/// Loop 不管「这是第几句用户话」，Runtime 才管。
/// </summary>
public sealed class AgentRuntime
{
    private readonly AgentLoop _loop;

    // 整段对话的真相：按时间排列的 input 项。
    // dump 命令打印的就是它。
    private readonly List<InputItem> _conversation = [];

    public AgentRuntime(AgentLoop loop) => _loop = loop;

    public event EventHandler<AgentEvent>? Event;

    public IReadOnlyList<InputItem> Conversation => _conversation;

    /// <summary>
    /// 处理用户的一句输入。内部可能触发多次 LLM HTTP（见 AgentLoop）。
    /// </summary>
    public async Task<string> RunTurnAsync(string userMessage, CancellationToken cancellationToken)
    {
        _conversation.Add(new UserMessageItem(userMessage));
        Raise(AgentEvent.TurnStarted(userMessage));

        try
        {
            var text = await _loop.RunAsync(_conversation, OnLoopEvent, cancellationToken).ConfigureAwait(false);
            Raise(AgentEvent.TurnFinished(text));
            return text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Raise(AgentEvent.Error(ex.Message));
            throw;
        }
    }

    /// <summary>清空 conversation。DELETE /conversation 会调它。</summary>
    public void Reset() => _conversation.Clear();

    /// <summary>
    /// 把当前 input 列表打成 JSON。GET /conversation 用这份。
    /// UnsafeRelaxedJsonEscaping 让中文和引号直接可见，便于对照 Responses 文档。
    /// </summary>
    public string DumpConversation()
    {
        var array = _conversation.Select(item => item.ToJson()).ToArray();
        return JsonSerializer.Serialize(array, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    // Loop 不知道 C# event，只认委托。这里做一层薄适配。
    private ValueTask OnLoopEvent(AgentEvent evt, CancellationToken cancellationToken)
    {
        Raise(evt);
        return ValueTask.CompletedTask;
    }

    private void Raise(AgentEvent evt) => Event?.Invoke(this, evt);
}
