using System.Text;
using System.Text.Json;

namespace AgentHarness.Llm;

/// <summary>
/// SSE 的 data JSON → <see cref="LlmStreamEvent"/>。
/// 单独拆出来是为了能不发 HTTP 就单测「协议翻译」。
///
/// 只处理 loop 用得着的几种 type，其余（response.created、in_progress、
/// output_item.done……）静默丢掉。完整事件表很长，教学项目不必全认。
/// </summary>
public static class ResponsesSseMapper
{
    public static IEnumerable<LlmStreamEvent> Map(SseEvent sse)
    {
        // 有的服务器在真正的 JSON 事件之后再发一行 data: [DONE]
        if (string.Equals(sse.Data, "[DONE]", StringComparison.Ordinal))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(sse.Data);
            // ToArray：把字符串拷出来，这样 using 释放 JsonDocument 也安全
            return MapDocument(sse.EventType, doc.RootElement).ToArray();
        }
        catch (JsonException ex)
        {
            return [new ResponseFailedEvent($"SSE JSON 无法解析: {ex.Message}")];
        }
    }

    private static IEnumerable<LlmStreamEvent> MapDocument(string eventType, JsonElement root)
    {
        // 优先信 data.type；缺了再退回 SSE 的 event: 字段
        var type = root.TryGetProperty("type", out var typeEl)
            ? typeEl.GetString()
            : eventType;

        switch (type)
        {
            case "response.output_text.delta":
                if (root.TryGetProperty("delta", out var textDelta))
                    yield return new TextDeltaEvent(textDelta.GetString() ?? "");
                break;

            case "response.output_item.added":
                // output 里可能是 message / function_call / reasoning……
                // 我们只在「新出现一个 function_call」时通知 UI 黄字。
                if (root.TryGetProperty("item", out var added)
                    && added.TryGetProperty("type", out var addedType)
                    && addedType.GetString() == "function_call")
                {
                    yield return new FunctionCallStartedEvent(
                        ReadString(added, "call_id"),
                        ReadString(added, "name"));
                }

                break;

            case "response.function_call_arguments.delta":
                // 官方字段是 item_id；有的实现也会带 call_id。两个都试。
                yield return new FunctionCallArgumentsDeltaEvent(
                    ReadString(root, "call_id", "item_id"),
                    root.TryGetProperty("delta", out var argsDelta) ? argsDelta.GetString() ?? "" : "");
                break;

            case "response.completed":
                // 决策点：完整 output[] 在这里。Loop 只看这一帧里的 function_call 列表。
                yield return ParseCompleted(root);
                break;

            case "response.failed":
            case "error":
                yield return new ResponseFailedEvent(ReadErrorMessage(root));
                break;
        }
    }

    /// <summary>
    /// 从 response.completed 里抽出「给用户看的文本」和「要点名的工具」。
    /// output 是数组，一轮里可以同时有 message 和若干 function_call。
    /// </summary>
    private static ResponseCompletedEvent ParseCompleted(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var response))
            return new ResponseCompletedEvent("", "", []);

        var id = ReadString(response, "id");
        var text = new StringBuilder();
        var calls = new List<FunctionCallItem>();

        if (response.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                var itemType = ReadString(item, "type");
                if (itemType == "function_call")
                {
                    calls.Add(new FunctionCallItem(
                        ReadString(item, "call_id"),
                        ReadString(item, "name"),
                        ReadString(item, "arguments")));
                }
                else if (itemType == "message" && item.TryGetProperty("content", out var content))
                {
                    AppendMessageText(content, text);
                }
                // reasoning 等其它 item 忽略
            }
        }

        return new ResponseCompletedEvent(id, text.ToString(), calls);
    }

    private static void AppendMessageText(JsonElement content, StringBuilder text)
    {
        // content 有时是字符串，有时是 [{type:output_text,text:"..."}]
        if (content.ValueKind == JsonValueKind.String)
        {
            text.Append(content.GetString());
            return;
        }

        if (content.ValueKind != JsonValueKind.Array)
            return;

        foreach (var part in content.EnumerateArray())
        {
            var partType = ReadString(part, "type");
            if (partType is "output_text" or "text")
                text.Append(ReadString(part, "text"));
        }
    }

    private static string ReadErrorMessage(JsonElement root)
    {
        // 错误可能在 response.error.message、error.message 或顶层 message
        if (root.TryGetProperty("response", out var response)
            && response.TryGetProperty("error", out var error)
            && error.TryGetProperty("message", out var nested))
            return nested.GetString() ?? root.GetRawText();

        if (root.TryGetProperty("error", out var top) && top.TryGetProperty("message", out var msg))
            return msg.GetString() ?? top.GetRawText();

        if (root.TryGetProperty("message", out var plain))
            return plain.GetString() ?? root.GetRawText();

        return root.GetRawText();
    }

    private static string ReadString(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (el.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? "";
        }

        return "";
    }
}
