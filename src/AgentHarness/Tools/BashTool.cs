using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentHarness.Tools;

/// <summary>
/// 在 workspace 目录里跑一条 shell。教学实现只做三件护栏：
/// 工作目录锁在 Root、15 秒超时、输出截断到 <see cref="MaxOutputChars"/>。
///
/// 没有进程沙箱：命令仍然能读家目录、访问网络。不要拿它跑不信任的指令。
/// </summary>
public sealed class BashTool(WorkspaceRoot workspace) : ITool
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    public const int MaxOutputChars = 16 * 1024;

    public string Name => "bash";

    public string Description =>
        "Run one shell command with cwd = workspace. Captures stdout and stderr. Times out after 15s.";

    public JsonObject Parameters => new()
    {
        ["command"] = new JsonObject
        {
            ["type"] = "string",
            ["description"] = "A single command, e.g. ls -la"
        }
    };

    public async Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var command = arguments.GetProperty("command").GetString();
        if (string.IsNullOrWhiteSpace(command))
            return "ERROR: command is empty";

        var psi = CreateStartInfo(command);
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start())
            return "ERROR: failed to start process";

        // 必须先开始读 stdout/stderr，再 WaitForExit。
        // 否则管道缓冲区满了，子进程会卡住，我们这边也永远等不到退出。
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(DefaultTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 走到这里是超时，不是用户按了 Ctrl+C
            TryKill(process);
            return $"ERROR: timed out after {DefaultTimeout.TotalSeconds:0}s";
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return Format(process.ExitCode, stdout, stderr);
    }

    private ProcessStartInfo CreateStartInfo(string command)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workspace.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (OperatingSystem.IsWindows())
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command);
        }
        else
        {
            // -l：登录壳，能拿到较完整的 PATH；-c：后面整段是要执行的命令
            psi.FileName = File.Exists("/bin/bash") ? "/bin/bash" : "bash";
            psi.ArgumentList.Add("-lc");
            psi.ArgumentList.Add(command);
        }

        return psi;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // 已经退了
        }
    }

    private static string Format(int exitCode, string stdout, string stderr)
    {
        // 模型既要看到输出，也要看到退出码，否则分不清「命令失败」和「没东西可打印」
        var sb = new StringBuilder();
        sb.Append("exit=").Append(exitCode);
        if (stdout.Length > 0)
            sb.Append("\nstdout:\n").Append(Clip(stdout));
        if (stderr.Length > 0)
            sb.Append("\nstderr:\n").Append(Clip(stderr));
        if (stdout.Length == 0 && stderr.Length == 0)
            sb.Append("\n(no output)");
        return sb.ToString();
    }

    private static string Clip(string text)
        => text.Length <= MaxOutputChars ? text : text[..MaxOutputChars] + "\n... truncated";
}
