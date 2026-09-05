using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using AgentHarness.Tools;

namespace AgentHarness.Llm;

/// <summary>
/// 唯一的网络出口：POST {base}/responses，然后把 SSE 映射成 <see cref="LlmStreamEvent"/>。
///
/// 刻意不做的两件事（方便对照 dump 看懂 loop）：
/// <list type="bullet">
/// <item>不用 previous_response_id —— 每次把完整 input 列表发回去</item>
/// <item>不用官方 SDK —— 请求体就是下面 BuildBody 拼出来的那一小段 JSON</item>
/// </list>
/// </summary>
public sealed class OpenAiResponsesClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly ToolRegistry _tools;

    public OpenAiResponsesClient(HttpClient http, string model, ToolRegistry tools)
    {
        _http = http;
        _model = model;
        _tools = tools;
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "responses")
        {
            Content = new StringContent(BuildBody(request), Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        // ResponseHeadersRead：头一到就开始读流，不必等整段 SSE 收完。
        // 这是「流式」和「等完整 JSON」在 HttpClient 上的分界。
        using var httpResponse = await _http
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            yield return new ResponseFailedEvent(
                $"HTTP {(int)httpResponse.StatusCode} {httpResponse.ReasonPhrase}: {errorBody}");
            yield break;
        }

        var stream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var sse in SseReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(sse.Data, "[DONE]", StringComparison.Ordinal))
                yield break;

            foreach (var mapped in ResponsesSseMapper.Map(sse))
                yield return mapped;
        }
    }

    /// <summary>
    /// 手写 JSON，避免隐藏字段。对照官方文档时可以在这里打断点看 body。
    /// stream=true 是 SSE 的开关；tools 每轮都带，模型才知道自己能调什么。
    /// </summary>
    private string BuildBody(LlmRequest request)
    {
        var input = new JsonArray();
        foreach (var item in request.Input)
            input.Add(item.ToJson());

        var body = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(request.Model) ? _model : request.Model,
            ["instructions"] = request.Instructions,
            ["input"] = input,
            ["tools"] = _tools.ToResponsesTools(),
            ["stream"] = true
        };

        return body.ToJsonString();
    }
}
