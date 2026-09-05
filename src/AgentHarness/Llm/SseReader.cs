using System.Runtime.CompilerServices;
using System.Text;

namespace AgentHarness.Llm;

/// <summary>
/// 一条已经分帧好的 SSE 事件。规范：
/// https://html.spec.whatwg.org/multipage/server-sent-events.html
///
/// OpenAI 典型长这样（注意中间的空行才是边界）：
/// <code>
/// event: response.output_text.delta
/// data: {"type":"response.output_text.delta","delta":"Hi"}
///
/// event: response.completed
/// data: {"type":"response.completed","response":{...}}
///
/// </code>
/// event 字段和 data JSON 里的 type 通常相同。本项目以 data.type 为准。
/// </summary>
public sealed record SseEvent(string EventType, string Data);

/// <summary>
/// 把原始字节流拆成 SSE 事件。这里不解析 JSON，只负责「空行分帧」。
/// JSON 翻译交给 <see cref="ResponsesSseMapper"/>，这样两件事可以分开测。
/// </summary>
public static class SseReader
{
    public static async IAsyncEnumerable<SseEvent> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var data = new StringBuilder();
        var eventType = "message"; // SSE 默认事件名；OpenAI 一般会显式给 event:

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                // 流结束时如果最后没有空行，把已经攒起来的 data 也交出去
                if (data.Length > 0)
                    yield return new SseEvent(eventType, data.ToString());
                yield break;
            }

            // 空行 = 一条事件结束。这是 SSE 的分帧规则，不是 JSON 的。
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return new SseEvent(eventType, data.ToString());
                    data.Clear();
                    eventType = "message";
                }

                continue;
            }

            // ':' 开头是注释，代理常用它做 keep-alive，必须丢掉。
            if (line[0] == ':')
                continue;

            // "field: value"；冒号后的第一个空格按规范要去掉。
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' '))
                value = value[1..];

            switch (field)
            {
                case "event":
                    eventType = value;
                    break;
                case "data":
                    // 同一事件可以有多行 data:，用 \n 拼起来（规范如此）
                    if (data.Length > 0)
                        data.Append('\n');
                    data.Append(value);
                    break;
                // id / retry 用来做断线续传，这个小 harness 用不上
            }
        }
    }
}
