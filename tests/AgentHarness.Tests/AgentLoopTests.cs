using System.Runtime.CompilerServices;
using AgentHarness.Llm;
using AgentHarness.Runtime;
using AgentHarness.Tools;

namespace AgentHarness.Tests;

/// <summary>
/// 用写死的两拍脚本代替真模型：第一拍 function_call，第二拍纯文本。
/// 用来锁住「先回传 call 再追加 output」的顺序。
/// </summary>
public class AgentLoopTests
{
    [Fact]
    public async Task Calls_tool_then_stops_on_text()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "hello.txt"), "hi-from-disk");
            var workspace = new WorkspaceRoot(dir.FullName);
            var tools = new ToolRegistry([new ReadFileTool(workspace)]);

            var llm = new ScriptedLlmClient(
                new ResponseCompletedEvent("r1", "", [
                    new FunctionCallItem("call_1", "read_file", """{"path":"hello.txt"}""")
                ]),
                new ResponseCompletedEvent("r2", "file said hi-from-disk", []));

            var loop = new AgentLoop(llm, tools, new AgentLoopOptions { MaxTurns = 4 });
            var conversation = new List<InputItem> { new UserMessageItem("read hello") };
            var kinds = new List<AgentEventKind>();

            var text = await loop.RunAsync(conversation, (evt, _) =>
            {
                kinds.Add(evt.Kind);
                return ValueTask.CompletedTask;
            }, CancellationToken.None);

            Assert.Equal("file said hi-from-disk", text);
            Assert.Contains(conversation, item => item is FunctionCallItem);
            Assert.Contains(conversation, item => item is FunctionCallOutputItem output && output.Output.Contains("hi-from-disk"));
            Assert.Contains(AgentEventKind.ToolFinished, kinds);
            Assert.Equal(2, llm.RequestCount);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Stops_when_max_turns_hit()
    {
        var tools = new ToolRegistry([]);
        var llm = new ScriptedLlmClient(
            _ => new ResponseCompletedEvent("r", "", [
                new FunctionCallItem("c", "missing", "{}")
            ]));

        var loop = new AgentLoop(llm, tools, new AgentLoopOptions { MaxTurns = 2 });
        var conversation = new List<InputItem> { new UserMessageItem("loop forever") };

        var text = await loop.RunAsync(conversation, (_, _) => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Contains("MaxTurns", text, StringComparison.Ordinal);
        Assert.Equal(2, llm.RequestCount);
    }

    private sealed class ScriptedLlmClient : ILlmClient
    {
        private readonly Func<LlmRequest, ResponseCompletedEvent>[] _script;
        private readonly bool _repeatLast;
        private int _index;

        public int RequestCount { get; private set; }

        public ScriptedLlmClient(params ResponseCompletedEvent[] turns)
        {
            _script = turns.Select(t => (Func<LlmRequest, ResponseCompletedEvent>)(_ => t)).ToArray();
        }

        public ScriptedLlmClient(Func<LlmRequest, ResponseCompletedEvent> everyTurn)
        {
            _script = [everyTurn];
            _repeatLast = true;
        }

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            RequestCount++;
            var turn = _repeatLast
                ? _script[0](request)
                : _script[_index++](request);

            if (turn.Text.Length > 0)
                yield return new TextDeltaEvent(turn.Text);
            foreach (var call in turn.FunctionCalls)
                yield return new FunctionCallStartedEvent(call.CallId, call.Name);
            yield return turn;
        }
    }
}
