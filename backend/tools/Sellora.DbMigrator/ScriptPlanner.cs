namespace Sellora.DbMigrator;

/// <summary>What the journal knows about a script in this database.</summary>
public sealed record JournalEntry(string Name, string Status, string? Checksum);

public enum StepAction { Run, Skip }

public sealed record PlanStep(SqlScript Script, StepAction Action, string Reason);

public sealed record MigrationPlan(IReadOnlyList<PlanStep> Steps, IReadOnlyList<string> Errors);

/// <summary>
/// Decides, without touching the database, which scripts run and in which order (spec rules R1, R5–R8).
/// Errors here stop the run before anything is executed.
/// </summary>
public static class ScriptPlanner
{
    public static MigrationPlan Plan(IEnumerable<SqlScript> scripts, IReadOnlyDictionary<string, JournalEntry> journal)
    {
        var steps = new List<PlanStep>();
        var errors = new List<string>();

        var ordered = scripts
            .OrderBy(s => s.Kind) // Data → Repeatable → Validation (enum order)
            .ThenBy(s => s.Name, StringComparer.Ordinal);

        foreach (var script in ordered)
        {
            journal.TryGetValue(script.Name, out var entry);
            switch (script.Kind)
            {
                case ScriptKind.Data when entry is { Status: JournalStatus.Succeeded } && entry.Checksum != script.Checksum:
                    errors.Add($"applied script modified: {script.Name}. Succeeded scripts are final; restore the file and add a new script.");
                    break;
                case ScriptKind.Data when entry is { Status: JournalStatus.Succeeded }:
                    steps.Add(new PlanStep(script, StepAction.Skip, "already applied"));
                    break;
                case ScriptKind.Data when entry is { Status: JournalStatus.Baselined }:
                    steps.Add(new PlanStep(script, StepAction.Skip, "baselined"));
                    break;
                case ScriptKind.Data:
                    steps.Add(new PlanStep(script, StepAction.Run, entry is { Status: JournalStatus.Failed } ? "retry after failure" : "new"));
                    break;
                case ScriptKind.Repeatable when entry is { Status: JournalStatus.Succeeded } && entry.Checksum == script.Checksum:
                    steps.Add(new PlanStep(script, StepAction.Skip, "unchanged"));
                    break;
                case ScriptKind.Repeatable:
                    steps.Add(new PlanStep(script, StepAction.Run, entry is null ? "new" : entry.Status == JournalStatus.Failed ? "retry after failure" : "changed"));
                    break;
                case ScriptKind.Validation:
                    steps.Add(new PlanStep(script, StepAction.Run, "runs every time"));
                    break;
            }
        }

        return new MigrationPlan(steps, errors);
    }
}

public static class JournalStatus
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Baselined = "Baselined";
    public const string Passed = "Passed";
    public const string Warning = "Warning";
    public const string Removed = "Removed";
}
