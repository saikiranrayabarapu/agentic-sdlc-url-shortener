using System.Text.RegularExpressions;
using AgenticSdlc.Core.Model;
using AgenticSdlc.Core.Workspace;

namespace AgenticSdlc.Core.Policy;

public enum PolicySeverity { Warn, Block }

public sealed record PolicyFinding(string RuleId, PolicySeverity Severity, string? Path, string Message);

public sealed class PolicyResult
{
    public List<PolicyFinding> Findings { get; } = new();
    public bool Blocked => Findings.Any(f => f.Severity == PolicySeverity.Block);
    public ImpactLevel Impact { get; set; } = ImpactLevel.Low;
    public List<string> RulesEvaluated { get; } = new();
}

/// <summary>
/// Policy guardrails evaluated before any agent-proposed change touches the workspace.
/// Rules are deliberately explicit and small so they can be defended in a change-control review:
///   SEC-001 secrets in code          (block)
///   SEC-002 dangerous APIs           (block)
///   SEC-003 wide-open CORS           (warn)
///   CMP-001 dependency allow-list    (block)
///   CMP-002 protected paths          (block)
///   CHG-001 change-size threshold    (raises impact to High -> human approval)
///   CHG-002 deletions                (raises impact to High)
///   CHG-003 modifying existing files (raises impact to at least Medium)
/// </summary>
public sealed class PolicyEngine
{
    private static readonly Regex[] SecretPatterns =
    {
        new(@"AKIA[0-9A-Z]{16}", RegexOptions.Compiled),                                  // AWS access key
        new(@"-----BEGIN (RSA |EC |OPENSSH |)PRIVATE KEY-----", RegexOptions.Compiled),     // private keys
        new(@"(?i)(password|pwd|secret|api[_-]?key|token)\s*[:=]\s*[""'][^""'\s]{8,}[""']", RegexOptions.Compiled), // hard-coded credential literals
        new(@"sk-ant-[A-Za-z0-9_-]{20,}", RegexOptions.Compiled),                         // Anthropic API key
        new(@"ghp_[A-Za-z0-9]{30,}", RegexOptions.Compiled),                               // GitHub token
    };

    private static readonly (Regex Pattern, string Why)[] DangerousApis =
    {
        (new(@"\bProcess\.Start\(", RegexOptions.Compiled), "spawning processes from a web service is not permitted"),
        (new(@"\[DllImport\(", RegexOptions.Compiled), "native interop is not permitted in generated service code"),
        (new(@"Assembly\.Load(From|File)?\(", RegexOptions.Compiled), "dynamic assembly loading is not permitted"),
        (new(@"\bEnvironment\.Exit\(", RegexOptions.Compiled), "hard process exit bypasses graceful shutdown"),
        (new(@"TrustServerCertificate\s*=\s*true", RegexOptions.Compiled | RegexOptions.IgnoreCase), "TLS validation must not be disabled"),
    };

    private static readonly Regex PackageRef = new(@"<PackageReference\s+Include=""([^""]+)""", RegexOptions.Compiled);

    public IReadOnlyList<string> AllowedPackagePrefixes { get; init; } = new[]
    {
        "Microsoft.", "System.", "xunit", "coverlet.", "FluentAssertions", "Swashbuckle.", "Serilog", "Polly"
    };

    public IReadOnlyList<string> ProtectedPathPatterns { get; init; } = new[]
    {
        ".github/", "nuget.config", "appsettings.Production.json", ".git/"
    };

    public int HighImpactFileThreshold { get; init; } = 12;

    public PolicyResult EvaluateChangeSet(ChangeSet changeSet, Workspace.Workspace workspace, ScenarioKind scenarioKind)
    {
        var result = new PolicyResult();
        result.RulesEvaluated.AddRange(new[] { "SEC-001", "SEC-002", "SEC-003", "CMP-001", "CMP-002", "CHG-001", "CHG-002", "CHG-003" });

        var modifiesExisting = false;
        var deletes = 0;

        foreach (var file in changeSet.Files)
        {
            var path = file.Path.Replace('\\', '/');

            // CMP-002 protected paths
            if (ProtectedPathPatterns.Any(p => path.Contains(p, StringComparison.OrdinalIgnoreCase)))
                result.Findings.Add(new("CMP-002", PolicySeverity.Block, path, "Change touches a protected path (CI, package sources, production config)."));

            // Path containment is enforced by the workspace too; surfacing it here produces an auditable verdict.
            try { workspace.Resolve(path); }
            catch (InvalidOperationException ex)
            {
                result.Findings.Add(new("CMP-002", PolicySeverity.Block, path, ex.Message));
                continue; // nothing else can be evaluated safely for a path outside the sandbox
            }

            if (file.Action.Equals("delete", StringComparison.OrdinalIgnoreCase)) { deletes++; continue; }
            if (workspace.Exists(path)) modifiesExisting = true;

            var content = file.Content ?? string.Empty;

            // SEC-001 secrets
            foreach (var pattern in SecretPatterns)
            {
                if (pattern.IsMatch(content))
                    result.Findings.Add(new("SEC-001", PolicySeverity.Block, path, $"Possible hard-coded secret matches /{pattern}/."));
            }

            // SEC-002 dangerous APIs (only for service source, tests are allowed to spawn processes)
            if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !path.Contains("Tests", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var (pattern, why) in DangerousApis)
                {
                    if (pattern.IsMatch(content))
                        result.Findings.Add(new("SEC-002", PolicySeverity.Block, path, $"Dangerous API: {why}."));
                }
                // SEC-003 wide-open CORS
                if (content.Contains("AllowAnyOrigin()", StringComparison.Ordinal))
                    result.Findings.Add(new("SEC-003", PolicySeverity.Warn, path, "CORS allows any origin; restrict before production."));
            }

            // CMP-001 dependency allow-list
            if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Match m in PackageRef.Matches(content))
                {
                    var pkg = m.Groups[1].Value;
                    if (!AllowedPackagePrefixes.Any(p => pkg.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                        result.Findings.Add(new("CMP-001", PolicySeverity.Block, path, $"Package '{pkg}' is not on the approved dependency list."));
                }
            }
        }

        // Change-control impact classification
        var impact = ParseImpact(changeSet.Impact);
        if (changeSet.Files.Count > HighImpactFileThreshold && scenarioKind != ScenarioKind.Greenfield)
        {
            impact = ImpactLevel.High;
            result.Findings.Add(new("CHG-001", PolicySeverity.Warn, null, $"{changeSet.Files.Count} files changed exceeds threshold {HighImpactFileThreshold}; classified High impact."));
        }
        if (deletes > 0)
        {
            impact = ImpactLevel.High;
            result.Findings.Add(new("CHG-002", PolicySeverity.Warn, null, $"{deletes} file deletion(s); classified High impact."));
        }
        if (modifiesExisting && impact < ImpactLevel.Medium)
        {
            impact = ImpactLevel.Medium;
            result.Findings.Add(new("CHG-003", PolicySeverity.Warn, null, "Existing files are modified; classified at least Medium impact."));
        }
        result.Impact = impact;
        return result;
    }

    public static ImpactLevel ParseImpact(string? s) => s?.ToLowerInvariant() switch
    {
        "high" => ImpactLevel.High,
        "medium" => ImpactLevel.Medium,
        _ => ImpactLevel.Low
    };
}
