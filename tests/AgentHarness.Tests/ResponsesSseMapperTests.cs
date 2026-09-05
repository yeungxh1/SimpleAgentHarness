using AgentHarness.Llm;

namespace AgentHarness.Tests;

/// <summary>不发 HTTP，只验证 OpenAI 那几种 type 能翻译成本地 LlmStreamEvent。</summary>
public class ResponsesSseMapperTests
{
    [Fact]
    public void Maps_text_delta()
    {
        var ev = ResponsesSseMapper.Map(new SseEvent(
            "response.output_text.delta",
            """{"type":"response.output_text.delta","delta":"Hello"}""")).Single();

        var text = Assert.IsType<TextDeltaEvent>(ev);
        Assert.Equal("Hello", text.Delta);
    }

    [Fact]
    public void Maps_function_call_and_completed_snapshot()
    {
        var started = ResponsesSseMapper.Map(new SseEvent(
            "response.output_item.added",
            """
            {"type":"response.output_item.added","item":{"type":"function_call","call_id":"call_1","name":"read_file","arguments":""}}
            """)).Single();

        var start = Assert.IsType<FunctionCallStartedEvent>(started);
        Assert.Equal("call_1", start.CallId);
        Assert.Equal("read_file", start.Name);

        var completed = ResponsesSseMapper.Map(new SseEvent(
            "response.completed",
            """
            {
              "type": "response.completed",
              "response": {
                "id": "resp_1",
                "output": [
                  {
                    "type": "function_call",
                    "call_id": "call_1",
                    "name": "read_file",
                    "arguments": "{\"path\":\"hello.txt\"}"
                  }
                ]
              }
            }
            """)).Single();

        var done = Assert.IsType<ResponseCompletedEvent>(completed);
        Assert.Equal("resp_1", done.ResponseId);
        var call = Assert.Single(done.FunctionCalls);
        Assert.Equal("hello.txt", System.Text.Json.JsonDocument.Parse(call.Arguments).RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public void Maps_assistant_message_text()
    {
        var ev = ResponsesSseMapper.Map(new SseEvent(
            "response.completed",
            """
            {
              "type": "response.completed",
              "response": {
                "id": "resp_2",
                "output": [
                  {
                    "type": "message",
                    "role": "assistant",
                    "content": [{ "type": "output_text", "text": "done" }]
                  }
                ]
              }
            }
            """)).Single();

        var done = Assert.IsType<ResponseCompletedEvent>(ev);
        Assert.Equal("done", done.Text);
        Assert.Empty(done.FunctionCalls);
    }
}
