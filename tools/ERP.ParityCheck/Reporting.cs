using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ERP.ParityCheck;

public enum Provider { SqlServer, PostgreSql }

public enum DbContextKind { Master, AppDb }

/// <summary>
/// The only outcomes a check may report. FailInfrastructure (D-k9) separates "the environment
/// prevented verification" — an unreachable endpoint, a refused login, a failed CREATE
/// DATABASE — from FailDefect, "the providers did not behave as approved". Both are failures.
/// </summary>
public enum Classification
{
    Pass,
    FailDefect,
    FailInfrastructure,
    ExpectedDifference,
    SkippedNoEndpoint,
    Deferred,
}

public enum RunMode { Live, Simulation }

/// <summary>One check outcome. <see cref="Scope"/> is e.g. "SqlServer/Master" or "Cross/Master".</summary>
public sealed record CheckResult(string Id, string Title, string Scope, Classification Classification, string Detail);

/// <summary>
/// EXIT CODES — explicit, and independent of console text so they can be asserted.
///
///   0  Success     — every check in the full matrix ran; none failed, none skipped.
///                     L-10 is always DEFERRED, so DB-03 is still reported PARTIALLY OPEN.
///   1  Failure     — at least one check is FAIL — DEFECT (a parity assertion failed) or
///                     FAIL — INFRASTRUCTURE (e.g. a configured endpoint could not be reached).
///   2  Incomplete  — nothing failed, but at least one check was SKIPPED — NO ENDPOINT.
///                     Live parity is NOT verified.
///   3  Usage       — invalid command line.
///   4  Safety      — a safety guard refused to operate (existing target database,
///                     protected name, endpoint naming another database). Nothing was created.
///   5  Cleanup     — a disposable database created by this run could not be removed.
///
/// Precedence when several apply: 5 > 4 > 1 > 2 > 0.
/// </summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int Incomplete = 2;
    public const int Usage = 3;
    public const int SafetyRefusal = 4;
    public const int CleanupFailure = 5;
}

public sealed class Report
{
    public Report(RunMode mode) => Mode = mode;

    public RunMode Mode { get; }

    public List<CheckResult> Results { get; } = [];

    /// <summary>Non-secret environment evidence: endpoints (host/port only), container state.</summary>
    public List<string> Evidence { get; } = [];

    public List<string> SafetyRefusals { get; } = [];

    public List<string> CleanupFailures { get; } = [];

    public void Add(CheckResult result) => Results.Add(result with { Detail = Redact.Secrets(result.Detail) });

    public int Count(Classification c) => Results.Count(r => r.Classification == c);

    private int Failures => Count(Classification.FailDefect) + Count(Classification.FailInfrastructure);

    public int ExitCode =>
        CleanupFailures.Count > 0 ? ExitCodes.CleanupFailure
        : SafetyRefusals.Count > 0 ? ExitCodes.SafetyRefusal
        : Failures > 0 ? ExitCodes.Failure
        : Count(Classification.SkippedNoEndpoint) > 0 ? ExitCodes.Incomplete
        : ExitCodes.Success;

    /// <summary>
    /// The one-line verdict. Only a LIVE run with zero failures and zero skips may say the
    /// selected live checks were verified — and even then DB-03 stays PARTIALLY OPEN (L-10).
    /// </summary>
    public string Verdict
    {
        get
        {
            var prefix = Mode == RunMode.Simulation ? "SIMULATION — NOT LIVE EVIDENCE. " : string.Empty;
            var coverage = CoverageNote();
            return ExitCode switch
            {
                ExitCodes.CleanupFailure => prefix + "CLEANUP FAILURE — disposable database(s) may remain. See recovery instructions. DB-03 PARTIALLY OPEN.",
                ExitCodes.SafetyRefusal => prefix + "SAFETY REFUSAL — no live verification performed. DB-03 PARTIALLY OPEN.",
                ExitCodes.Failure => prefix +
                    $"FAILED — {Count(Classification.FailDefect)} parity defect(s), {Count(Classification.FailInfrastructure)} infrastructure failure(s); " +
                    $"live parity NOT verified.{coverage} DB-03 PARTIALLY OPEN.",
                ExitCodes.Incomplete => prefix + $"INCOMPLETE — {Count(Classification.SkippedNoEndpoint)} check(s) skipped; live parity NOT verified.{coverage} DB-03 PARTIALLY OPEN.",
                _ => Mode == RunMode.Simulation
                    ? prefix + "Simulated matrix classified without failures or skips. No live parity claim. L-10 DEFERRED. DB-03 PARTIALLY OPEN."
                    : "Full live matrix executed: no failures, no skips. L-10 DEFERRED (upgrade path not yet testable). DB-03 PARTIALLY OPEN.",
            };
        }
    }

    private string CoverageNote()
    {
        var providers = Results
            .Where(r => r.Classification is Classification.Pass or Classification.FailDefect or Classification.ExpectedDifference)
            .Select(r => r.Scope.Split('/')[0])
            .Where(s => s is "SqlServer" or "PostgreSql")
            .Distinct()
            .ToList();
        return providers.Count == 1 ? $" PARTIAL COVERAGE — only {providers[0]} exercised." : string.Empty;
    }

    public static string Label(Classification c) => c switch
    {
        Classification.Pass => "PASS",
        Classification.FailDefect => "FAIL — DEFECT",
        Classification.FailInfrastructure => "FAIL — INFRASTRUCTURE",
        Classification.ExpectedDifference => "EXPECTED DIFFERENCE — RECORDED",
        Classification.SkippedNoEndpoint => "SKIPPED — NO ENDPOINT",
        Classification.Deferred => "DEFERRED",
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    // ── Sinks (Low-1) ─────────────────────────────────────────────────────────
    // Every collection is public and can be appended to directly, so redaction is applied
    // HERE — at the only two places a report leaves the process — not just on Add().

    private static string S(string text) => Redact.Secrets(text);

    private IEnumerable<CheckResult> RedactedResults() =>
        Results.Select(r => r with { Id = S(r.Id), Title = S(r.Title), Scope = S(r.Scope), Detail = S(r.Detail) });

    public string Render()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Mode == RunMode.Simulation
            ? "=== ERP.ParityCheck — SIMULATION (no database contacted; NOT live evidence) ==="
            : "=== ERP.ParityCheck — LIVE provider parity ===");
        foreach (var e in Evidence) sb.AppendLine("evidence: " + S(e));
        foreach (var r in RedactedResults().OrderBy(r => r.Id, StringComparer.Ordinal).ThenBy(r => r.Scope, StringComparer.Ordinal))
            sb.AppendLine($"[{Label(r.Classification)}] {r.Id} {r.Scope} — {r.Title}: {r.Detail}");
        foreach (var s in SafetyRefusals) sb.AppendLine("SAFETY REFUSAL: " + S(s));
        foreach (var c in CleanupFailures) sb.AppendLine("CLEANUP FAILURE: " + S(c));
        sb.AppendLine($"counts: PASS={Count(Classification.Pass)} FAIL-DEFECT={Count(Classification.FailDefect)} " +
                      $"FAIL-INFRASTRUCTURE={Count(Classification.FailInfrastructure)} " +
                      $"EXPECTED-DIFFERENCE={Count(Classification.ExpectedDifference)} SKIPPED={Count(Classification.SkippedNoEndpoint)} " +
                      $"DEFERRED={Count(Classification.Deferred)}");
        sb.AppendLine($"verdict: {Verdict}");
        sb.AppendLine($"exit code: {ExitCode}");
        return sb.ToString();
    }

    public string ToJson() => JsonSerializer.Serialize(new
    {
        mode = Mode.ToString(),
        results = RedactedResults().Select(r => new { r.Id, r.Title, r.Scope, classification = Label(r.Classification), r.Detail }),
        counts = Enum.GetValues<Classification>().ToDictionary(Label, Count),
        evidence = Evidence.Select(S),
        safetyRefusals = SafetyRefusals.Select(S),
        cleanupFailures = CleanupFailures.Select(S),
        verdict = Verdict,
        exitCode = ExitCode,
    }, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never });
}

/// <summary>Last-line defence: strips credential-looking fragments from any reported text.</summary>
public static partial class Redact
{
    [GeneratedRegex(@"(?i)\b(password|pwd|user id|uid|username|user)\s*=\s*[^;""]*")]
    private static partial Regex CredentialPattern();

    public static string Secrets(string text) => CredentialPattern().Replace(text, m => m.Value.Split('=')[0] + "=<redacted>");
}
