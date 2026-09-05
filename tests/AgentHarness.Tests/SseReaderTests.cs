using System.Text;
using AgentHarness.Llm;

namespace AgentHarness.Tests;

/// <summary>只测「空行分帧」，不测 JSON。对照 SseReader.cs 顶部的示例最容易看懂。</summary>
public class SseReaderTests
{
    [Fact]
    public async Task Splits_events_on_blank_line()
    {
        const string raw = """
            event: response.output_text.delta
            data: {"type":"response.output_text.delta","delta":"Hi"}

            event: response.completed
            data: {"type":"response.completed"}

            """;

        var events = await ReadAll(raw);

        Assert.Equal(2, events.Count);
        Assert.Equal("response.output_text.delta", events[0].EventType);
        Assert.Contains("Hi", events[0].Data, StringComparison.Ordinal);
        Assert.Equal("response.completed", events[1].EventType);
    }

    [Fact]
    public async Task Ignores_comment_lines()
    {
        const string raw = """
            : keep-alive

            data: {"type":"response.output_text.delta","delta":"A"}

            """;

        var events = await ReadAll(raw);

        Assert.Single(events);
        Assert.Contains("\"A\"", events[0].Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Joins_multiline_data()
    {
        const string raw = """
            data: {"a":
            data: 1}

            """;

        var events = await ReadAll(raw);

        Assert.Single(events);
        Assert.Equal("{\"a\":\n1}", events[0].Data);
    }

    private static async Task<List<SseEvent>> ReadAll(string raw)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        var list = new List<SseEvent>();
        await foreach (var ev in SseReader.ReadAsync(stream))
            list.Add(ev);
        return list;
    }
}
