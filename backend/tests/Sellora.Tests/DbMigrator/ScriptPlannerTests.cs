using Sellora.DbMigrator;

namespace Sellora.Tests.DbMigrator;

/// <summary>Spec docs/features/db-release-automation.md rules R1, R5–R8 (planning happens before anything is executed).</summary>
public class ScriptPlannerTests
{
    private static SqlScript Script(string name, ScriptKind kind, string sql = "SELECT 1;")
    {
        var errors = new List<string>();
        var script = ScriptCatalog.Parse(name, kind, sql, errors);
        Assert.Empty(errors);
        return script!;
    }

    private static Dictionary<string, JournalEntry> Journal(params JournalEntry[] entries) =>
        entries.ToDictionary(e => e.Name, StringComparer.Ordinal);

    [Fact]
    public void Orders_data_then_repeatable_then_validation_each_by_name()
    {
        var plan = ScriptPlanner.Plan(
        [
            Script("V__b.sql", ScriptKind.Validation),
            Script("R__seed.sql", ScriptKind.Repeatable),
            Script("2026.11.19.001__second.sql", ScriptKind.Data),
            Script("V__a.sql", ScriptKind.Validation),
            Script("2026.11.18.001__first.sql", ScriptKind.Data),
        ], Journal());

        Assert.Empty(plan.Errors);
        Assert.Equal(
            ["2026.11.18.001__first.sql", "2026.11.19.001__second.sql", "R__seed.sql", "V__a.sql", "V__b.sql"],
            plan.Steps.Select(s => s.Script.Name));
    }

    [Fact]
    public void Succeeded_data_script_is_skipped_when_unchanged()
    {
        var script = Script("2026.11.18.001__x.sql", ScriptKind.Data);
        var plan = ScriptPlanner.Plan([script], Journal(new JournalEntry(script.Name, JournalStatus.Succeeded, script.Checksum)));

        var step = Assert.Single(plan.Steps);
        Assert.Equal(StepAction.Skip, step.Action);
    }

    [Fact]
    public void Modified_succeeded_data_script_is_an_error_and_nothing_is_planned_to_run()
    {
        var script = Script("2026.11.18.001__x.sql", ScriptKind.Data, "SELECT 2;");
        var plan = ScriptPlanner.Plan([script], Journal(new JournalEntry(script.Name, JournalStatus.Succeeded, "old-checksum")));

        Assert.Contains(plan.Errors, e => e.StartsWith("applied script modified: 2026.11.18.001__x.sql"));
    }

    [Fact]
    public void Failed_data_script_runs_again_even_when_fixed_in_place()
    {
        var script = Script("2026.11.18.001__x.sql", ScriptKind.Data, "SELECT 2;");
        var plan = ScriptPlanner.Plan([script], Journal(new JournalEntry(script.Name, JournalStatus.Failed, null)));

        var step = Assert.Single(plan.Steps);
        Assert.Equal(StepAction.Run, step.Action);
        Assert.Equal("retry after failure", step.Reason);
    }

    [Fact]
    public void Baselined_data_script_is_skipped()
    {
        var script = Script("2026.11.18.001__x.sql", ScriptKind.Data);
        var plan = ScriptPlanner.Plan([script], Journal(new JournalEntry(script.Name, JournalStatus.Baselined, null)));

        Assert.Equal(StepAction.Skip, Assert.Single(plan.Steps).Action);
    }

    [Theory]
    [InlineData(true, StepAction.Skip)]
    [InlineData(false, StepAction.Run)]
    public void Repeatable_script_runs_only_when_its_checksum_changed(bool sameChecksum, StepAction expected)
    {
        var script = Script("R__seed.sql", ScriptKind.Repeatable);
        var plan = ScriptPlanner.Plan([script],
            Journal(new JournalEntry(script.Name, JournalStatus.Succeeded, sameChecksum ? script.Checksum : "other")));

        Assert.Equal(expected, Assert.Single(plan.Steps).Action);
    }

    [Fact]
    public void Validation_script_always_runs()
    {
        var script = Script("V__check.sql", ScriptKind.Validation);
        var plan = ScriptPlanner.Plan([script], Journal(new JournalEntry(script.Name, JournalStatus.Passed, script.Checksum)));

        Assert.Equal(StepAction.Run, Assert.Single(plan.Steps).Action);
    }
}

public class ScriptCatalogTests
{
    [Fact]
    public void Reads_header_options()
    {
        var errors = new List<string>();
        var script = ScriptCatalog.Parse("V__check.sql", ScriptKind.Validation, """
            -- sellora:description Every stock row has a branch
            -- sellora:timeout 120
            -- sellora:lock-timeout 5
            -- sellora:on-violation block
            SELECT 1 WHERE false;
            """, errors);

        Assert.Empty(errors);
        Assert.Equal("Every stock row has a branch", script!.Description);
        Assert.Equal(TimeSpan.FromSeconds(120), script.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(5), script.LockTimeout);
        Assert.Equal(ViolationMode.Block, script.OnViolation);
    }

    [Fact]
    public void Defaults_to_ten_minute_timeout_and_a_transaction()
    {
        var script = ScriptCatalog.Parse("2026.11.18.001__x.sql", ScriptKind.Data, "SELECT 1;", []);

        Assert.Equal(TimeSpan.FromMinutes(10), script!.Timeout);
        Assert.True(script.UseTransaction);
    }

    [Fact]
    public void Checksum_ignores_line_endings_and_bom()
    {
        var unix = ScriptCatalog.Parse("R__a.sql", ScriptKind.Repeatable, "SELECT 1;\nSELECT 2;\n", [])!;
        var windows = ScriptCatalog.Parse("R__a.sql", ScriptKind.Repeatable, "﻿SELECT 1;\r\nSELECT 2;\r\n", [])!;

        Assert.Equal(unix.Checksum, windows.Checksum);
    }

    [Theory]
    [InlineData("\\echo hello\nSELECT 1;", "psql meta-commands")]
    [InlineData("-- sellora:timeuot 10\nSELECT 1;", "unknown header")]
    [InlineData("-- sellora:timeout ten\nSELECT 1;", "whole number of seconds")]
    [InlineData("-- sellora:on-violation block\nSELECT 1;", "only applies to validation scripts")]
    [InlineData("\n\n", "file is empty")]
    public void Rejects_invalid_scripts(string sql, string expectedError)
    {
        var errors = new List<string>();
        var script = ScriptCatalog.Parse("2026.11.18.001__x.sql", ScriptKind.Data, sql, errors);

        Assert.Null(script);
        Assert.Contains(errors, e => e.Contains(expectedError));
    }

    [Fact]
    public void Load_rejects_names_that_do_not_follow_the_convention()
    {
        var root = Directory.CreateTempSubdirectory("sellora-migrator-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "scripts"));
            File.WriteAllText(Path.Combine(root.FullName, "scripts", "fix.sql"), "SELECT 1;");
            File.WriteAllText(Path.Combine(root.FullName, "scripts", "2026.11.18.001__ok-name.sql"), "SELECT 1;");
            File.WriteAllText(Path.Combine(root.FullName, "scripts", ".gitkeep"), "");
            File.WriteAllText(Path.Combine(root.FullName, "scripts", ".DS_Store"), "x");

            var (scripts, errors) = ScriptCatalog.Load(root.FullName);

            Assert.Equal("2026.11.18.001__ok-name.sql", Assert.Single(scripts).Name);
            Assert.Contains(errors, e => e.StartsWith("fix.sql: name must look like"));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
