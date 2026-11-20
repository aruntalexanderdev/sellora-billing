using Sellora.Domain.Platform;
using Sellora.Infrastructure.Security;
using Sellora.Infrastructure.Services;
using Sellora.Tests.TestData;

namespace Sellora.Tests.Platform;

/// <summary>Platform Console data reset: the switch is off by default and refuses everything while off; the service runs the
/// repository's reset script unchanged apart from the four operator values, and fails closed when the script is not in its
/// committed, disarmed form. Running the script itself needs PostgreSQL: see PlatformDataResetApiTests (integration).</summary>
public class DataResetServiceTests
{
    private static string RepositoryScript()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "scripts", "sql", "dev-reset-fresh-setup.sql");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException("scripts/sql/dev-reset-fresh-setup.sql not found above " + AppContext.BaseDirectory);
    }

    private static string Lf(string text) => text.Replace("\r\n", "\n");

    private static DataResetService Service(bool enabled) =>
        new(TestDb.NewInMemory(), Microsoft.Extensions.Options.Options.Create(new PlatformOptions { DataResetEnabled = enabled }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DataResetService>.Instance);

    [Fact]
    public void Switch_IsOffByDefault()
    {
        Assert.False(new PlatformOptions().DataResetEnabled);
        Assert.False(Service(enabled: false).IsEnabled);
        Assert.True(Service(enabled: true).IsEnabled);
    }

    [Fact]
    public async Task SwitchOff_PreviewAndReset_AreRefusedBeforeTheDatabaseIsTouched()
    {
        var service = Service(enabled: false);

        var preview = await Assert.ThrowsAsync<DataResetDisabledException>(() => service.PreviewAsync(new DataResetOptions()));
        var reset = await Assert.ThrowsAsync<DataResetDisabledException>(() => service.ResetAsync(new DataResetOptions(), "any", Guid.NewGuid(), null));

        Assert.Equal("data_reset_disabled", preview.Code);
        Assert.Equal("data_reset_disabled", reset.Code);
    }

    [Fact]
    public void Options_ByDefault_KeepCustomersAndSuppliers_AndRemoveTheRest()
    {
        var options = new DataResetOptions();

        Assert.Equal("", options.WipeList);
        Assert.Equal("", options.KeepList);
        Assert.Equal(["customers", "suppliers"], options.KeptMasters);
    }

    [Fact]
    public void Options_MapToTheScriptTokens()
    {
        var everything = new DataResetOptions(WipeCustomers: true, WipeSuppliers: true, KeepTaxes: true, KeepCategories: true, KeepExpenseCategories: true, KeepAccounts: true);

        Assert.Equal("customers,suppliers", everything.WipeList);
        Assert.Equal("taxes,categories,expense_categories,accounts", everything.KeepList);
        Assert.Equal(["accounts", "categories", "expense_categories", "taxes"], everything.KeptMasters);
        Assert.Equal("suppliers", new DataResetOptions(WipeSuppliers: true).WipeList);
        Assert.Equal(["customers", "taxes"], new DataResetOptions(WipeSuppliers: true, KeepTaxes: true).KeptMasters);
    }

    [Fact]
    public void EmbeddedScript_IsTheRepositoryScript()
    {
        Assert.Equal(Lf(RepositoryScript()), Lf(DataResetScript.Read()));
    }

    [Fact]
    public void Arm_FillsInOnlyTheFourOperatorValues()
    {
        var script = Lf(DataResetScript.Read());

        var armed = DataResetScript.Arm(script, "sellora_dev", apply: true, new DataResetOptions(WipeCustomers: true, KeepTaxes: true, KeepAccounts: true));

        var before = script.Split('\n');
        var after = armed.Split('\n');
        Assert.Equal(before.Length, after.Length);
        var changed = before.Zip(after).Where(pair => pair.First != pair.Second).Select(pair => pair.Second.Trim()).ToList();
        Assert.Equal(4, changed.Count);
        Assert.StartsWith("v_confirm_database constant text    := 'sellora_dev';", changed[0]);
        Assert.StartsWith("v_apply            constant boolean := true;", changed[1]);
        Assert.StartsWith("v_wipe             constant text    := 'customers';", changed[2]);
        Assert.StartsWith("v_keep             constant text    := 'taxes,accounts';", changed[3]);
    }

    [Fact]
    public void Arm_ForAPreview_KeepsApplyFalse()
    {
        var armed = DataResetScript.Arm(DataResetScript.Read(), "sellora_dev", apply: false, new DataResetOptions());

        Assert.Matches(@"v_apply\s+constant boolean\s+:= false;", armed);
        Assert.DoesNotMatch(@"v_apply\s+constant boolean\s+:= true;", armed);
        Assert.Matches(@"v_wipe\s+constant text\s+:= '';", armed);
        Assert.Matches(@"v_keep\s+constant text\s+:= '';", armed);
    }

    [Fact]
    public void Arm_DoublesQuotes_AndRefusesANameThatCouldEndTheDollarQuotedBlock()
    {
        var script = DataResetScript.Read();

        Assert.Contains(":= 'o''brien';", DataResetScript.Arm(script, "o'brien", apply: false, new DataResetOptions()));
        Assert.Throws<DataResetRefusedException>(() => DataResetScript.Arm(script, "a$$ ; DROP", apply: false, new DataResetOptions()));
        Assert.Throws<DataResetRefusedException>(() => DataResetScript.Arm(script, "two\nlines", apply: false, new DataResetOptions()));
    }

    [Theory]
    [InlineData("'TYPE-THE-DATABASE-NAME-HERE'", "'already_typed'")]
    [InlineData("constant boolean := false;", "constant boolean := true;")]
    [InlineData("v_wipe             constant text    := '';", "v_wipe             constant text    := 'customers';")]
    [InlineData("v_keep             constant text    := '';", "v_keep             constant text    := 'taxes';")]
    public void Arm_FailsClosed_WhenAValueLineIsNotInItsCommittedDisarmedForm(string committed, string tampered)
    {
        var script = DataResetScript.Read();
        Assert.Contains(committed, script);

        Assert.Throws<InvalidOperationException>(() => DataResetScript.Arm(script.Replace(committed, tampered), "sellora_dev", apply: true, new DataResetOptions()));
    }

    /// <summary>The service reads the script's result sheet by position and recognises its outcome by the first words of the RESULT
    /// row; the audit row takes the actor and request id from two session settings. Pin all of it, so an edit of the script cannot
    /// silently break the Platform Console.</summary>
    [Fact]
    public void Script_KeepsTheContractTheServiceDependsOn()
    {
        var script = Lf(DataResetScript.Read());

        Assert.Contains("v_status := format('" + DataResetScript.CheckOnlyPrefix + " - ", script);
        Assert.Contains("v_status := format('" + DataResetScript.DonePrefix + " - ", script);
        Assert.Contains("VALUES (0, 'RESULT', v_status);", script);
        Assert.Contains("SELECT area AS \"Area\", table_name AS \"Table\", rows_before AS \"Rows before\", rows_after AS \"Rows after\", action AS \"Result / action\"\nFROM pg_temp.sellora_reset_report ORDER BY seq;", script);
        Assert.Contains("cast(nullif(current_setting('sellora.reset_actor_user_id', true), '') AS uuid)", script);
        Assert.Contains("coalesce(nullif(current_setting('sellora.reset_correlation_id', true), ''), 'ops-script')", script);
        Assert.Contains("IF coalesce(current_setting('sellora.allow_dev_reset', true), '') <> 'on' THEN", script);
    }
}
