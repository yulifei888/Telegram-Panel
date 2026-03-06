using System.Diagnostics;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TelegramPanel.Core.Services.Telegram;

public static class TdataSessionBridge
{
    private static readonly SemaphoreSlim SetupLock = new(1, 1);
    private static readonly string RuntimeDir = ResolveRuntimeDir();

    private const int NodeCheckTimeoutMs = 10_000;
    private const int SetupTimeoutMs = 180_000;
    private const int ConvertTimeoutMs = 60_000;

    private static string ResolveRuntimeDir()
    {
        var fromEnv = Environment.GetEnvironmentVariable("TELEGRAM_PANEL_TDATA_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv.Trim();
        return Path.Combine(Path.GetTempPath(), "telegram-panel-tdata-runtime");
    }

private const string NodeScript = """
import { convertFromTdata, convertToTelethonSession } from '@mtcute/convert';

const inputPath = process.argv[process.argv.length - 1];

try {
  const session = await convertFromTdata({ path: inputPath, ignoreVersion: true });
  const sessionString = convertToTelethonSession(session);
  const userId = session?.self?.userId ?? null;
  console.log(JSON.stringify({ ok: true, sessionString, userId }));
} catch (error) {
  const message = error?.stack ? String(error.stack) : String(error);
  console.log(JSON.stringify({ ok: false, error: message }));
  process.exit(1);
}
""";

    private const string NodeScriptTelethonToTdata = """
import { convertFromTelethonSession, convertToTdata } from '@mtcute/convert';

const telethonSession = process.argv[process.argv.length - 3];
const userIdRaw = process.argv[process.argv.length - 2];
const outputDir = process.argv[process.argv.length - 1];

try {
  const session = convertFromTelethonSession(telethonSession);
  const userId = Number.parseInt(userIdRaw, 10);
  if (Number.isFinite(userId) && userId > 0) {
    session.self = {
      userId,
      isBot: false,
      isPremium: false,
      usernames: [],
    };
  }
  await convertToTdata(session, { path: outputDir });
  console.log(JSON.stringify({ ok: true }));
} catch (error) {
  const message = error?.stack ? String(error.stack) : String(error);
  console.log(JSON.stringify({ ok: false, error: message }));
  process.exit(1);
}
""";

    public readonly record struct ConvertResult(bool Ok, string? SessionString, long? UserId, string? Error)
    {
        public static ConvertResult Success(string sessionString, long? userId) => new(true, sessionString, userId, null);
        public static ConvertResult Fail(string error) => new(false, null, null, string.IsNullOrWhiteSpace(error) ? "未知错误" : error.Trim());
    }

    public readonly record struct ConvertToTdataResult(bool Ok, string? Error)
    {
        public static ConvertToTdataResult Success() => new(true, null);
        public static ConvertToTdataResult Fail(string error) => new(false, string.IsNullOrWhiteSpace(error) ? "未知错误" : error.Trim());
    }

    public static async Task<ConvertResult> TryConvertToTelethonStringSessionAsync(
        string tdataDirectory,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tdataDirectory) || !Directory.Exists(tdataDirectory))
            return ConvertResult.Fail("tdata 目录不存在");

        var runtimeReady = await EnsureRuntimeReadyAsync(logger, cancellationToken);
        if (!runtimeReady.Ok)
            return ConvertResult.Fail(runtimeReady.Error ?? "tdata 运行环境初始化失败");

        try
        {
            var run = await RunProcessAsync(
                fileName: "node",
                arguments: new[] { "--input-type=module", "-e", NodeScript, "--", Path.GetFullPath(tdataDirectory) },
                workingDirectory: RuntimeDir,
                timeoutMs: ConvertTimeoutMs,
                cancellationToken: cancellationToken);

            var output = PickJsonLine(run.StdOut);
            if (string.IsNullOrWhiteSpace(output))
            {
                var msg = string.IsNullOrWhiteSpace(run.StdErr) ? "node 输出为空" : run.StdErr.Trim();
                return ConvertResult.Fail(msg);
            }

            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True;

            if (!ok)
            {
                var err = root.TryGetProperty("error", out var errProp) && errProp.ValueKind == JsonValueKind.String
                    ? (errProp.GetString() ?? string.Empty)
                    : (string.IsNullOrWhiteSpace(run.StdErr) ? "未知错误" : run.StdErr.Trim());
                return ConvertResult.Fail(err);
            }

            if (!root.TryGetProperty("sessionString", out var sessionProp) || sessionProp.ValueKind != JsonValueKind.String)
                return ConvertResult.Fail("转换输出缺少 sessionString");

            var sessionString = (sessionProp.GetString() ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(sessionString))
                return ConvertResult.Fail("转换得到的 sessionString 为空");

            long? userId = null;
            if (root.TryGetProperty("userId", out var uidProp))
            {
                if (uidProp.ValueKind == JsonValueKind.Number && uidProp.TryGetInt64(out var uid))
                    userId = uid;
                else if (uidProp.ValueKind == JsonValueKind.String && long.TryParse(uidProp.GetString(), out var uidStr))
                    userId = uidStr;
            }

            return ConvertResult.Success(sessionString, userId);
        }
        catch (TimeoutException ex)
        {
            logger.LogWarning(ex, "Tdata conversion timed out: {TdataDir}", tdataDirectory);
            return ConvertResult.Fail("tdata 转换超时");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tdata conversion failed: {TdataDir}", tdataDirectory);
            return ConvertResult.Fail(ex.Message);
        }
    }

    public static async Task<ConvertToTdataResult> TryConvertTelethonStringSessionToTdataAsync(
        string telethonSessionString,
        long? userId,
        string outputTdataDirectory,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(telethonSessionString))
            return ConvertToTdataResult.Fail("telethon session_string 为空");
        if (string.IsNullOrWhiteSpace(outputTdataDirectory))
            return ConvertToTdataResult.Fail("tdata 输出目录为空");

        var runtimeReady = await EnsureRuntimeReadyAsync(logger, cancellationToken);
        if (!runtimeReady.Ok)
            return ConvertToTdataResult.Fail(runtimeReady.Error ?? "tdata 运行环境初始化失败");

        try
        {
            var normalizedSession = NormalizeTelethonSessionString(telethonSessionString);
            var absoluteOutputDir = Path.GetFullPath(outputTdataDirectory);
            if (Directory.Exists(absoluteOutputDir))
                Directory.Delete(absoluteOutputDir, recursive: true);
            Directory.CreateDirectory(absoluteOutputDir);

            var run = await RunProcessAsync(
                fileName: "node",
                arguments: new[] { "--input-type=module", "-e", NodeScriptTelethonToTdata, "--", normalizedSession, (userId ?? 0).ToString(), absoluteOutputDir },
                workingDirectory: RuntimeDir,
                timeoutMs: ConvertTimeoutMs,
                cancellationToken: cancellationToken);

            var output = PickJsonLine(run.StdOut);
            if (string.IsNullOrWhiteSpace(output))
            {
                var msg = string.IsNullOrWhiteSpace(run.StdErr) ? "node 输出为空" : run.StdErr.Trim();
                return ConvertToTdataResult.Fail(msg);
            }

            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True;
            if (!ok)
            {
                var err = root.TryGetProperty("error", out var errProp) && errProp.ValueKind == JsonValueKind.String
                    ? (errProp.GetString() ?? string.Empty)
                    : (string.IsNullOrWhiteSpace(run.StdErr) ? "未知错误" : run.StdErr.Trim());
                return ConvertToTdataResult.Fail(err);
            }

            if (!Directory.EnumerateFileSystemEntries(absoluteOutputDir).Any())
                return ConvertToTdataResult.Fail("tdata 输出目录为空");

            return ConvertToTdataResult.Success();
        }
        catch (TimeoutException ex)
        {
            logger.LogWarning(ex, "Telethon->tdata conversion timed out");
            return ConvertToTdataResult.Fail("Telethon->tdata 转换超时");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Telethon->tdata conversion failed");
            return ConvertToTdataResult.Fail(ex.Message);
        }
    }

    private static string NormalizeTelethonSessionString(string sessionString)
    {
        var trimmed = sessionString.Trim();
        if (trimmed.Length < 2 || !char.IsDigit(trimmed[0]))
            return trimmed;

        var body = trimmed[1..];
        var mod = body.Length % 4;
        if (mod == 0)
            return trimmed;
        if (mod == 1)
            return trimmed; // 非法长度，保持原样让下游报错更明确

        var padding = mod == 2 ? "==" : "=";
        return $"{trimmed}{padding}";
    }

    private static async Task<(bool Ok, string? Error)> EnsureRuntimeReadyAsync(ILogger logger, CancellationToken cancellationToken)
    {
        await SetupLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(RuntimeDir);

            if (HasRequiredPackages())
                return (true, null);

            var nodeCheck = await RunProcessAsync(
                fileName: "node",
                arguments: new[] { "--version" },
                workingDirectory: RuntimeDir,
                timeoutMs: NodeCheckTimeoutMs,
                cancellationToken: cancellationToken);
            if (nodeCheck.ExitCode != 0)
            {
                var err = string.IsNullOrWhiteSpace(nodeCheck.StdErr) ? "无法执行 node 命令" : nodeCheck.StdErr.Trim();
                return (false, $"Node.js 不可用：{err}");
            }

            var packageJson = Path.Combine(RuntimeDir, "package.json");
            if (!File.Exists(packageJson))
            {
                const string packageJsonContent = """
{
  "name": "telegram-panel-tdata-runtime",
  "private": true,
  "type": "module"
}
""";
                await File.WriteAllTextAsync(packageJson, packageJsonContent, Encoding.UTF8, cancellationToken);
            }

            if (!HasRequiredPackages())
            {
                logger.LogInformation("Installing tdata converter runtime dependencies in {RuntimeDir}", RuntimeDir);
                var install = await RunProcessAsync(
                    fileName: "npm",
                    arguments: new[] { "install", "--silent", "@mtcute/convert", "@mtcute/node" },
                    workingDirectory: RuntimeDir,
                    timeoutMs: SetupTimeoutMs,
                    cancellationToken: cancellationToken);
                if (install.ExitCode != 0)
                {
                    var err = string.IsNullOrWhiteSpace(install.StdErr) ? install.StdOut : install.StdErr;
                    return (false, $"npm 安装 tdata 依赖失败：{(err ?? string.Empty).Trim()}");
                }
            }

            if (!HasRequiredPackages())
                return (false, "tdata 依赖安装后仍不可用");

            return (true, null);
        }
        catch (TimeoutException ex)
        {
            logger.LogWarning(ex, "Preparing tdata runtime timed out");
            return (false, "准备 tdata 运行环境超时");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Preparing tdata runtime failed");
            return (false, ex.Message);
        }
        finally
        {
            SetupLock.Release();
        }
    }

    private static bool HasRequiredPackages()
    {
        if (!Directory.Exists(RuntimeDir))
            return false;

        var convertPkg = Path.Combine(RuntimeDir, "node_modules", "@mtcute", "convert", "package.json");
        var nodePkg = Path.Combine(RuntimeDir, "node_modules", "@mtcute", "node", "package.json");
        return File.Exists(convertPkg) && File.Exists(nodePkg);
    }

    private static string? PickJsonLine(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;

        var lines = stdout
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (line.StartsWith("{", StringComparison.Ordinal) && line.EndsWith("}", StringComparison.Ordinal))
                return line;
        }

        return lines.Count > 0 ? lines[^1] : null;
    }

    private readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr);

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyCollection<string> arguments,
        string workingDirectory,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            throw new InvalidOperationException("进程工作目录不能为空");

        Directory.CreateDirectory(workingDirectory);

        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
                stdOut.AppendLine(args.Data);
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
                stdErr.AppendLine(args.Data);
        };

        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"无法启动进程：{fileName}");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"无法启动进程 `{fileName}`，请确认容器/系统已安装并加入 PATH。原始错误：{ex.Message}",
                ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore kill failure
            }

            throw new TimeoutException($"{fileName} 执行超时（>{timeoutMs}ms）");
        }

        return new ProcessResult(process.ExitCode, stdOut.ToString(), stdErr.ToString());
    }
}
