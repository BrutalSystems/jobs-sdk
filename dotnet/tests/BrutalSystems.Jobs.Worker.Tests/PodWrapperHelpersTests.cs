using Xunit;

namespace BrutalSystems.Jobs.Worker.Tests;

public class PodWrapperHelpersTests
{
    [Fact]
    public void ArgsToEnv_uppercases_and_skips_handler()
    {
        var env = PodWrapper.ArgsToEnv(new Dictionary<string, object?>
        {
            ["lead_id"] = "123", ["handler"] = "score",
        });
        Assert.Equal("123", env["JOBS_ARG_LEAD_ID"]);
        Assert.False(env.ContainsKey("JOBS_ARG_HANDLER"));
    }

    [Fact]
    public void ArgsToFlags_maps_kebab_and_bools()
    {
        var flags = PodWrapper.ArgsToFlags(new Dictionary<string, object?>
        {
            ["lead_id"] = "123", ["verbose"] = true, ["dry_run"] = false, ["handler"] = "x",
        });
        Assert.Contains("--lead-id", flags);
        var flagsList = flags.ToList();
        Assert.Equal("123", flagsList[flagsList.IndexOf("--lead-id") + 1]);
        Assert.Contains("--verbose", flags);
        Assert.DoesNotContain("--dry-run", flags);
        Assert.DoesNotContain("--handler", flags);
    }

    [Fact]
    public void ExtractSummary_parses_marker_line_else_null()
    {
        var s = PodWrapper.ExtractSummary("__JOBS_SUMMARY__{\"processed\":42}");
        Assert.NotNull(s);
        Assert.Equal(42, ((System.Text.Json.JsonElement)s!["processed"]!).GetInt32());
        Assert.Null(PodWrapper.ExtractSummary("just a log line"));
        Assert.Null(PodWrapper.ExtractSummary("__JOBS_SUMMARY__{bad json"));
    }
}
