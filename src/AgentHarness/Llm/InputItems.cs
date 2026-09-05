using System.Text.Json.Nodes;

namespace AgentHarness.Llm;

/// <summary>
/// 发给 Responses API 的一条 input。
///
/// 和 Chat Completions 的差别：对话不是「user / assistant 两条消息轮流」，
/// 而是三种东西按时间排在同一个列表里。跑完一轮「读 hello.txt」之后 dump，你会看到：
/// <code>
/// [
///   { "type": "message",              "role": "user", "content": "读一下 hello.txt" },
///   { "type": "function_call",        "call_id": "call_1", "name": "read_file", "arguments": "{...}" },
///   { "type": "function_call_output", "call_id": "call_1", "output": "Hello from ..." }
/// ]
/// </code>
/// 下一轮请求把整份列表再发回去。本项目故意不用 previous_response_id，就是为了让这份列表可见。
/// </summary>
public abstract record InputItem
{
    public abstract JsonObject ToJson();
}

/// <summary>用户在 REPL 里敲下的一句话。</summary>
public sealed record UserMessageItem(string Text) : InputItem
{
    public override JsonObject ToJson() => new()
    {
        ["type"] = "message",
        ["role"] = "user",
        ["content"] = Text
    };
}

/// <summary>
/// 模型在上一轮决定调用的工具。
/// 下一轮必须原样回传：模型要靠 call_id 把「我叫过这个」和「本地跑出来的结果」对上。
/// Arguments 是 JSON 字符串，不是对象（API 就是这样规定的）。
/// </summary>
public sealed record FunctionCallItem(string CallId, string Name, string Arguments) : InputItem
{
    public override JsonObject ToJson() => new()
    {
        ["type"] = "function_call",
        ["call_id"] = CallId,
        ["name"] = Name,
        ["arguments"] = Arguments
    };
}

/// <summary>
/// 本地跑完工具后贴回去的结果。Output 是普通字符串（文件内容、bash 输出、或 ERROR: ...）。
/// 工具失败也写成字符串，而不是抛异常 —— 模型需要「看见」失败才能改参数重试。
/// </summary>
public sealed record FunctionCallOutputItem(string CallId, string Output) : InputItem
{
    public override JsonObject ToJson() => new()
    {
        ["type"] = "function_call_output",
        ["call_id"] = CallId,
        ["output"] = Output
    };
}
