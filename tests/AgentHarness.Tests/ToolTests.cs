using System.Text.Json;
using AgentHarness.Tools;

namespace AgentHarness.Tests;

/// <summary>工具本身的护栏：路径逃逸、恰好一次替换、读写回环、bash 的 cwd。</summary>
public class ToolTests
{
    [Fact]
    public void Workspace_rejects_path_escape()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var workspace = new WorkspaceRoot(dir.FullName);
            Assert.Throws<InvalidOperationException>(() => workspace.Resolve("../outside.txt"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Edit_replaces_exactly_one_match()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var workspace = new WorkspaceRoot(dir.FullName);
            var path = workspace.Resolve("note.txt");
            await File.WriteAllTextAsync(path, "aaa bbb aaa");
            var tool = new EditFileTool(workspace);

            var many = await tool.ExecuteAsync(Json("""{"path":"note.txt","old_text":"aaa","new_text":"x"}"""), CancellationToken.None);
            Assert.Contains("more than once", many, StringComparison.Ordinal);

            var once = await tool.ExecuteAsync(Json("""{"path":"note.txt","old_text":"bbb","new_text":"ccc"}"""), CancellationToken.None);
            Assert.Equal("edited note.txt", once);
            Assert.Equal("aaa ccc aaa", await File.ReadAllTextAsync(path));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Write_then_read_roundtrip()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var workspace = new WorkspaceRoot(dir.FullName);
            var registry = new ToolRegistry([new WriteFileTool(workspace), new ReadFileTool(workspace)]);

            var written = await registry.ExecuteAsync("write_file", """{"path":"a/b.txt","contents":"hello"}""", CancellationToken.None);
            Assert.Contains("wrote", written, StringComparison.Ordinal);

            var read = await registry.ExecuteAsync("read_file", """{"path":"a/b.txt"}""", CancellationToken.None);
            Assert.Equal("hello", read);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Bash_runs_in_workspace()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var workspace = new WorkspaceRoot(dir.FullName);
            await File.WriteAllTextAsync(workspace.Resolve("x.txt"), "ok");
            var tool = new BashTool(workspace);
            var output = await tool.ExecuteAsync(Json("""{"command":"ls"}"""), CancellationToken.None);
            Assert.Contains("x.txt", output, StringComparison.Ordinal);
            Assert.Contains("exit=0", output, StringComparison.Ordinal);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();
}
