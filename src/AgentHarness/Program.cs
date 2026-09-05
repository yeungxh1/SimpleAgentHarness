using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using AgentHarness;
using AgentHarness.Llm;
using AgentHarness.Runtime;
using AgentHarness.Tools;

// =============================================================================
// Minimal Web API 入口。Harness 核心没变，只是把「谁听门铃」换掉了：
//
//   以前：REPL 读键盘 → Runtime → EventPrinter 往控制台涂色
//   现在：POST /turns  → Runtime → 把 AgentEvent 写成 SSE 推给浏览器
//
// 装配顺序 = 建议阅读顺序：
//   1. WorkspaceRoot + 四个 Tool
//   2. OpenAiResponsesClient
//   3. AgentLoop
//   4. AgentRuntime          还是那一个单例，conversation 跨请求还在
//   5. MapPost("/turns")     新的订阅者：写 HTTP SSE
//
// 注意两层 SSE，别混：
//   - 模型 → 我们：OpenAI Responses 的 stream（SseReader）
//   - 我们 → 浏览器：把 AgentEvent 再写成 text/event-stream
// =============================================================================

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(ComposeHost);
builder.Services.AddSingleton(sp => sp.GetRequiredService<AgentHost>().Runtime);
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "AgentHarness";
        document.Info.Version = "v1";
        document.Info.Description =
            "最小 agent harness。POST /turns 的响应是 text/event-stream，每条 data 是一个 AgentEvent。";
        return Task.CompletedTask;
    });
});

var app = builder.Build();
app.MapOpenApi();
app.UseSwaggerUI(options =>
{
    options.DocumentTitle = "AgentHarness";
    options.SwaggerEndpoint("/openapi/v1.json", "AgentHarness v1");
});

app.MapGet("/", () => Results.Redirect("/swagger"))
    .ExcludeFromDescription();

app.MapPost("/turns", RunTurnAsync)
    .WithName("RunTurn")
    .WithTags("AgentHarness")
    .WithSummary("跑一个用户回合")
    .WithDescription("响应是 text/event-stream。每条 event: agent，data 是一个 AgentEvent JSON。Swagger 的 Try it out 会一直挂到回合结束。")
    .Accepts<TurnRequest>("application/json")
    .Produces<string>(StatusCodes.Status200OK, contentType: "text/event-stream")
    .Produces(StatusCodes.Status400BadRequest)
    .Produces(StatusCodes.Status409Conflict);

app.MapGet("/conversation", (AgentRuntime runtime) =>
        Results.Content(runtime.DumpConversation(), "application/json"))
    .WithName("GetConversation")
    .WithTags("AgentHarness")
    .WithSummary("导出当前 conversation JSON")
    .Produces(StatusCodes.Status200OK, contentType: "application/json");

app.MapDelete("/conversation", (AgentRuntime runtime) =>
    {
        runtime.Reset();
        return Results.NoContent();
    })
    .WithName("ResetConversation")
    .WithTags("AgentHarness")
    .WithSummary("清空对话")
    .Produces(StatusCodes.Status204NoContent);

app.MapGet("/info", (AgentHost host) => new HarnessInfo(host.WorkspaceRoot, host.Model, host.BaseUrl))
    .WithName("GetInfo")
    .WithTags("AgentHarness")
    .WithSummary("workspace / model / baseUrl（不含 Key）")
    .Produces<HarnessInfo>();

app.Run();

static AgentHost ComposeHost(IServiceProvider services)
{
    var config = services.GetRequiredService<IConfiguration>();
    var openAi = config.GetSection("OpenAI");
    var apiKey = openAi["ApiKey"];
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        throw new InvalidOperationException(
            "appsettings.json 里 OpenAI:ApiKey 是空的。打开 src/AgentHarness/appsettings.json 填上再 F5。");
    }

    var env = services.GetRequiredService<IWebHostEnvironment>();
    var workspacePath = ResolveWorkspace(env, config["Workspace"] ?? "workspace");
    var workspace = new WorkspaceRoot(workspacePath);
    EnsureSampleFile(workspace);

    var tools = new ToolRegistry([
        new ReadFileTool(workspace),
        new WriteFileTool(workspace),
        new EditFileTool(workspace),
        new BashTool(workspace)
    ]);

    var http = new HttpClient
    {
        BaseAddress = new Uri(AppendSlash(openAi["BaseUrl"] ?? "https://api.openai.com/v1")),
        Timeout = Timeout.InfiniteTimeSpan
    };
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

    var model = openAi["Model"] ?? "gpt-4.1";
    var maxTurns = int.TryParse(config["MaxTurns"], out var n) ? n : 8;
    var llm = new OpenAiResponsesClient(http, model, tools);
    var loop = new AgentLoop(llm, tools, new AgentLoopOptions { Model = model, MaxTurns = maxTurns });

    return new AgentHost
    {
        Runtime = new AgentRuntime(loop),
        WorkspaceRoot = workspace.Root,
        Model = model,
        BaseUrl = http.BaseAddress!.ToString()
    };
}

static string AppendSlash(string url) => url.EndsWith('/') ? url : url + "/";

// 相对路径优先用仓库根下的 workspace/（ContentRoot 是项目目录）。
static string ResolveWorkspace(IWebHostEnvironment env, string configured)
{
    if (Path.IsPathRooted(configured))
        return Path.GetFullPath(configured);

    var fromRepo = Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "..", configured));
    if (Directory.Exists(fromRepo))
        return fromRepo;

    return Path.GetFullPath(Path.Combine(env.ContentRootPath, configured));
}

static void EnsureSampleFile(WorkspaceRoot workspace)
{
    var hello = workspace.Resolve("hello.txt");
    if (!File.Exists(hello))
        File.WriteAllText(hello, "Hello from AgentHarness.\nThis file is here so you can try read_file.\n");
}

/// <summary>
/// 挂上门铃 → 跑 RunTurnAsync → 把每张纸条写成 SSE 行。
/// 这就是以前 EventPrinter 的位置：换了订阅者，Loop 一行没改。
/// </summary>
static async Task RunTurnAsync(TurnRequest body, AgentHost host, HttpResponse response, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(body.Message))
    {
        response.StatusCode = StatusCodes.Status400BadRequest;
        await response.WriteAsJsonAsync(new { error = "message 不能为空" }, ct);
        return;
    }

    if (!await host.Gate.WaitAsync(0, ct))
    {
        response.StatusCode = StatusCodes.Status409Conflict;
        await response.WriteAsJsonAsync(new { error = "上一个回合还在跑" }, ct);
        return;
    }

    // Channel：C# event 是同步回调，HTTP 写是异步。先把纸条丢进队列，再慢慢写出去。
    var channel = Channel.CreateUnbounded<AgentEvent>();
    void OnEvent(object? _, AgentEvent evt) => channel.Writer.TryWrite(evt);
    host.Runtime.Event += OnEvent;

    response.ContentType = "text/event-stream";
    response.Headers.CacheControl = "no-cache";

    try
    {
        var run = host.Runtime.RunTurnAsync(body.Message.Trim(), ct);
        _ = run.ContinueWith(_ => channel.Writer.TryComplete(), CancellationToken.None);

        await foreach (var evt in channel.Reader.ReadAllAsync(ct))
        {
            var json = JsonSerializer.Serialize(evt, AgentHost.EventJson);
            await response.WriteAsync($"event: agent\ndata: {json}\n\n", ct);
            await response.Body.FlushAsync(ct);
        }

        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
            // 客户端断开
        }
        catch (Exception)
        {
            // Runtime 已经 Raise 过 Error，流里有了
        }
    }
    finally
    {
        host.Runtime.Event -= OnEvent;
        host.Gate.Release();
    }
}
