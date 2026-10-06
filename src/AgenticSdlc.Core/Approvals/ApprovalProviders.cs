using System.Collections.Concurrent;
using AgenticSdlc.Core.Model;

namespace AgenticSdlc.Core.Approvals;

public sealed record ApprovalDecision(bool Approved, string Approver, string? Comment, bool Automated);

public sealed record ClarificationAnswers(Dictionary<string, string> Answers, string Approver, bool Automated);

/// <summary>
/// The human-in-the-loop boundary. The orchestrator blocks on these calls at governance checkpoints:
/// high-impact change sets, release readiness, and ambiguous requirements.
/// </summary>
public interface IApprovalProvider
{
    Task<ApprovalDecision> RequestApprovalAsync(RunState run, PendingApproval request, CancellationToken ct);
    Task<ClarificationAnswers> RequestClarificationAsync(RunState run, PendingClarification request, CancellationToken ct);
}

/// <summary>Approves everything and picks default assumptions. For CI and demos only; every decision is audited as automated.</summary>
public sealed class AutoApprovalProvider : IApprovalProvider
{
    public Task<ApprovalDecision> RequestApprovalAsync(RunState run, PendingApproval request, CancellationToken ct)
        => Task.FromResult(new ApprovalDecision(true, "auto-approve flag", "Approved automatically because the run was started with --auto-approve.", true));

    public Task<ClarificationAnswers> RequestClarificationAsync(RunState run, PendingClarification request, CancellationToken ct)
        => Task.FromResult(new ClarificationAnswers(
            request.Questions.ToDictionary(q => q.Id, q => q.DefaultAssumption),
            "auto-approve flag", true));
}

/// <summary>Interactive console prompts for a human operator.</summary>
public sealed class ConsoleApprovalProvider : IApprovalProvider
{
    private readonly TextReader _in;
    private readonly TextWriter _out;

    public ConsoleApprovalProvider(TextReader? input = null, TextWriter? output = null)
    {
        _in = input ?? Console.In;
        _out = output ?? Console.Out;
    }

    public Task<ApprovalDecision> RequestApprovalAsync(RunState run, PendingApproval request, CancellationToken ct)
    {
        _out.WriteLine();
        _out.WriteLine("┌──────────────────────────── HUMAN APPROVAL REQUIRED ────────────────────────────┐");
        _out.WriteLine($"  Run:    {run.RunId}");
        _out.WriteLine($"  Stage:  {request.NodeId}   Impact: {request.Impact}");
        _out.WriteLine($"  {request.Title}");
        foreach (var line in request.Summary.Split('\n')) _out.WriteLine("  " + line);
        _out.WriteLine("└──────────────────────────────────────────────────────────────────────────────────┘");
        _out.Write("Approve? [y/N] (optionally add a comment after a space): ");
        var line2 = _in.ReadLine() ?? "";
        var approved = line2.TrimStart().StartsWith("y", StringComparison.OrdinalIgnoreCase);
        var comment = line2.Length > 1 ? line2[1..].Trim() : null;
        var approver = Environment.UserName;
        return Task.FromResult(new ApprovalDecision(approved, approver, string.IsNullOrWhiteSpace(comment) ? null : comment, false));
    }

    public Task<ClarificationAnswers> RequestClarificationAsync(RunState run, PendingClarification request, CancellationToken ct)
    {
        _out.WriteLine();
        _out.WriteLine("┌──────────────────────── REQUIREMENT CLARIFICATION NEEDED ───────────────────────┐");
        var answers = new Dictionary<string, string>();
        foreach (var q in request.Questions)
        {
            _out.WriteLine($"  [{q.Id}] ({q.Impact} impact) {q.Question}");
            for (var i = 0; i < q.Options.Count; i++) _out.WriteLine($"     {i + 1}. {q.Options[i]}");
            _out.WriteLine($"     Enter = default: {q.DefaultAssumption}");
            _out.Write("  Choice: ");
            var input = (_in.ReadLine() ?? "").Trim();
            if (int.TryParse(input, out var idx) && idx >= 1 && idx <= q.Options.Count) answers[q.Id] = q.Options[idx - 1];
            else if (!string.IsNullOrWhiteSpace(input)) answers[q.Id] = input;
            else answers[q.Id] = q.DefaultAssumption;
        }
        _out.WriteLine("└──────────────────────────────────────────────────────────────────────────────────┘");
        return Task.FromResult(new ClarificationAnswers(answers, Environment.UserName, false));
    }
}

/// <summary>
/// Non-blocking provider for the HTTP host and dashboard: the run parks in AwaitingApproval / AwaitingClarification
/// and an API call resolves the pending request. An optional timeout turns an unanswered request into a safe-stop.
/// </summary>
public sealed class QueuedApprovalProvider : IApprovalProvider
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ApprovalDecision>> _approvals = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ClarificationAnswers>> _clarifications = new();

    public TimeSpan? Timeout { get; init; }

    public async Task<ApprovalDecision> RequestApprovalAsync(RunState run, PendingApproval request, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _approvals[request.Id] = tcs;
        try
        {
            return await WaitAsync(tcs.Task, ct);
        }
        finally
        {
            _approvals.TryRemove(request.Id, out _);
        }
    }

    public async Task<ClarificationAnswers> RequestClarificationAsync(RunState run, PendingClarification request, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ClarificationAnswers>(TaskCreationOptions.RunContinuationsAsynchronously);
        _clarifications[request.Id] = tcs;
        try
        {
            return await WaitAsync(tcs.Task, ct);
        }
        finally
        {
            _clarifications.TryRemove(request.Id, out _);
        }
    }

    public bool Resolve(string approvalId, ApprovalDecision decision)
        => _approvals.TryGetValue(approvalId, out var tcs) && tcs.TrySetResult(decision);

    public bool ResolveClarification(string clarificationId, ClarificationAnswers answers)
        => _clarifications.TryGetValue(clarificationId, out var tcs) && tcs.TrySetResult(answers);

    private async Task<T> WaitAsync<T>(Task<T> task, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (Timeout is { } t) cts.CancelAfter(t);
        var completed = await Task.WhenAny(task, Task.Delay(System.Threading.Timeout.Infinite, cts.Token));
        if (completed == task) return await task;
        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
        throw new TimeoutException("No human decision was received before the approval timeout; stopping safely.");
    }
}
