using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AgenticSdlc.Core.Llm;

/// <summary>
/// Live client that shells out to the Claude Code CLI (<c>claude -p</c>) and uses whatever login the CLI already has.
/// Useful on developer machines without an API key. The CLI path is taken from CLAUDE_CLI_PATH or discovered on PATH
/// and in the VS Code extension folder.
/// </summary>
public sealed class ClaudeCliLlmClient : ILlmClient
{
    private readonly string _cliPath;

    public ClaudeCliLlmClient(string? cliPath = null)
    {
        _cliPath = cliPath ?? Environment.GetEnvironmentVariable("CLAUDE_CLI_PATH") ?? Discover()
            ?? throw new LlmUnavailableException("Claude CLI not found. Set CLAUDE_CLI_PATH or use --mode replay.");
    }

    public string Name => "claude-cli";

    public static string? Discover()
    {
        var candidates = new List<string>();
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var dir in pathDirs)
        {
            candidates.Add(Path.Combine(dir, "claude.exe"));
            candidates.Add(Path.Combine(dir, "claude.cmd"));
            candidates.Add(Path.Combine(dir, "claude"));
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var ext = Path.Combine(home, ".vscode", "extensions");
        if (Directory.Exists(ext))
        {
            foreach (var dir in Directory.GetDirectories(ext, "anthropic.claude-code-*").OrderByDescending(d => d))
            {
                candidates.Add(Path.Combine(dir, "resources", "native-binary", "claude.exe"));
                candidates.Add(Path.Combine(dir, "resources", "native-binary", "claude"));
            }
        }
        candidates.Add(Path.Combine(home, ".claude", "local", "claude"));
        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var prompt = $"{request.SystemPrompt}\n\n---\n\n{request.UserPrompt}";

        var psi = new ProcessStartInfo
        {
            FileName = _cliPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("--max-turns");
        psi.ArgumentList.Add("1");
        // Running inside an existing Claude Code session would otherwise be refused as a nested session.
        psi.Environment.Remove("CLAUDECODE");
        psi.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");

        using var process = new Process { StartInfo = psi };
        if (!process.Start()) throw new LlmUnavailableException("Failed to start the Claude CLI process.");

        await process.StandardInput.WriteAsync(prompt);
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(6));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new LlmUnavailableException("Claude CLI timed out or was cancelled.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new LlmUnavailableException($"Claude CLI exited with {process.ExitCode}: {Truncate(stderr, 800)}");

        string text;
        string? model = null;
        int? inTok = null, outTok = null;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            text = root.TryGetProperty("result", out var r) ? r.GetString() ?? "" : stdout;
            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("input_tokens", out var it)) inTok = it.GetInt32();
                if (usage.TryGetProperty("output_tokens", out var ot)) outTok = ot.GetInt32();
            }
            if (root.TryGetProperty("modelUsage", out var mu) && mu.ValueKind == JsonValueKind.Object)
                model = mu.EnumerateObject().Select(p => p.Name).LastOrDefault();
        }
        catch (JsonException)
        {
            text = stdout;
        }

        return new LlmResponse
        {
            Text = text,
            Provider = Name,
            Model = model,
            InputTokens = inTok,
            OutputTokens = outTok,
            LatencyMs = (DateTime.UtcNow - started).TotalMilliseconds
        };
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
