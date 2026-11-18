namespace Sellora.DbMigrator;

// Sellora.DbMigrator — applies the release's SQL scripts and records every attempt in the ops journal.
// See docs/features/db-release-automation.md. Exit codes: 0 ok, 1 script/validation failed, 2 invalid release, 3 lock timeout,
// 4 unexpected (cannot connect, journal error, cancelled); a run left "Running" by 4 is closed by the next run.
//
//   SELLORA_DB_CONNECTION="Host=…;Database=…;Username=…;Password=…" \
//   dotnet Sellora.DbMigrator.dll --scripts backend/db --environment dev [--ef-bundle ./efbundle] [--plan [--ef-migrations-list ef-migrations.txt]]
//
// The connection string is read from SELLORA_DB_CONNECTION so it never appears in the process list or pipeline logs.
// Run metadata defaults to the GitHub Actions variables (GITHUB_SHA, GITHUB_ACTOR, run URL) when present.

// Explicit class (not top-level statements) so it never collides with Sellora.Api's global Program in test projects.
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var args_ = ParseArgs(args);
        if (args_ is null) return (int)ExitCode.InvalidRelease;

        var connection = Environment.GetEnvironmentVariable("SELLORA_DB_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
        {
            Console.Error.WriteLine("SELLORA_DB_CONNECTION is not set.");
            return (int)ExitCode.InvalidRelease;
        }

        string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
        string? Arg(string name) => args_.TryGetValue(name, out var v) ? v : null;

        var environment = Arg("environment") ?? "local";
        var pipelineUrl = Env("GITHUB_RUN_ID") is { } runId
            ? $"{Env("GITHUB_SERVER_URL")}/{Env("GITHUB_REPOSITORY")}/actions/runs/{runId}"
            : null;

        var trigger = Arg("trigger") ?? "Deploy";
        if (trigger is not ("Deploy" or "Retry"))
        {
            Console.Error.WriteLine("--trigger must be Deploy or Retry.");
            return (int)ExitCode.InvalidRelease;
        }
        if (trigger == "Retry" && string.IsNullOrWhiteSpace(Arg("reason")))
        {
            Console.Error.WriteLine("--reason is required for a retry.");
            return (int)ExitCode.InvalidRelease;
        }

        var validationMode = Arg("validation-default") switch
        {
            "block" => ViolationMode.Block,
            "warn" or null => ViolationMode.Warn,
            var other => (ViolationMode?)null,
        };
        if (validationMode is null)
        {
            Console.Error.WriteLine("--validation-default must be warn or block.");
            return (int)ExitCode.InvalidRelease;
        }

        var options = new MigratorOptions(
            connection,
            Arg("scripts") ?? "backend/db",
            new RunInfo(environment, Arg("commit") ?? Env("GITHUB_SHA"), Arg("version"), trigger, Arg("triggered-by") ?? Env("GITHUB_ACTOR") ?? Environment.UserName,
                Arg("reason"), pipelineUrl),
            validationMode.Value,
            TimeSpan.FromSeconds(int.TryParse(Arg("lock-wait"), out var wait) ? wait : 300),
            Arg("ef-bundle"),
            args_.ContainsKey("plan"),
            Arg("ef-migrations-list"));

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var runner = new MigrationRunner(options, Console.Out);
        ExitCode code;
        try
        {
            code = await runner.RunAsync(cts.Token);
        }
        catch (Exception ex)
        {
            // Message only: an Npgsql error never contains the password, but a stack trace adds nothing for the pipeline log.
            Console.Error.WriteLine($"✘ Migrator stopped: {ex.GetType().Name}: {ex.Message}");
            code = ExitCode.Unexpected;
        }
        WriteGitHubSummary(environment, code, runner.Outcomes, options.PlanOnly);
        return (int)code;
    }

    private static Dictionary<string, string>? ParseArgs(string[] argv)
    {
        string[] flags = ["plan"];
        string[] valued = ["scripts", "environment", "ef-bundle", "commit", "version", "trigger", "triggered-by", "reason", "validation-default", "lock-wait", "ef-migrations-list"];
        var result = new Dictionary<string, string>();
        for (var i = 0; i < argv.Length; i++)
        {
            var key = argv[i].StartsWith("--") ? argv[i][2..] : null;
            if (key is not null && flags.Contains(key)) { result[key] = "true"; continue; }
            if (key is not null && valued.Contains(key) && i + 1 < argv.Length) { result[key] = argv[++i]; continue; }
            Console.Error.WriteLine($"Unknown or incomplete argument '{argv[i]}'. Valued: --{string.Join(", --", valued)}. Flags: --{string.Join(", --", flags)}.");
            return null;
        }
        return result;
    }

    private static void WriteGitHubSummary(string environment, ExitCode code, IReadOnlyList<StepOutcome> outcomes, bool plan)
    {
        var path = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (string.IsNullOrEmpty(path)) return;
        using var w = File.AppendText(path);
        w.WriteLine(plan
            ? $"### Database plan · {environment} · {(code == ExitCode.Succeeded ? "✅ valid release" : $"❌ {code}")} (nothing executed)"
            : $"### Database scripts · {environment} · {(code == ExitCode.Succeeded ? "✅ succeeded" : $"❌ {code}")}");
        w.WriteLine();
        w.WriteLine("| Script | Kind | Status | Duration | Rows | Detail |");
        w.WriteLine("|---|---|---|---|---|---|");
        foreach (var o in outcomes)
            w.WriteLine($"| `{o.Name}` | {o.Kind} | {o.Status} | {(o.Duration is { } d ? $"{d.TotalMilliseconds:0} ms" : "")} | {o.Rows} | {System.Net.WebUtility.HtmlEncode(o.Detail?.Replace("\n", " "))?.Replace("|", "\\|").Replace("`", "'")} |");
    }
}
