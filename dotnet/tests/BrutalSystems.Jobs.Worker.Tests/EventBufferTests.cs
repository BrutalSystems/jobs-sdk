using System.Text.Json;
using Xunit;

namespace BrutalSystems.Jobs.Worker.Tests;

public class EventBufferTests
{
    [Fact]
    public void Write_appends_one_json_line_per_event()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jobs-buf-{Guid.NewGuid():N}", "buf.jsonl");
        var buf = new EventBuffer(path);
        buf.Write(new Dictionary<string, object?> { ["kind"] = "start_run", ["job_id"] = "j1" });
        buf.Write(new Dictionary<string, object?> { ["kind"] = "finish_run", ["job_id"] = "j1" });

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Equal("start_run", JsonDocument.Parse(lines[0]).RootElement.GetProperty("kind").GetString());
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public void Write_creates_parent_directory_if_missing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jobs-buf-{Guid.NewGuid():N}", "nested", "buf.jsonl");
        var buf = new EventBuffer(path);
        buf.Write(new Dictionary<string, object?> { ["kind"] = "start_run" });

        Assert.True(File.Exists(path));
        Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(path))!, recursive: true);
    }

    [Fact]
    public void Write_produces_valid_json_per_line()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jobs-buf-{Guid.NewGuid():N}.jsonl");
        var buf = new EventBuffer(path);
        buf.Write(new Dictionary<string, object?> { ["kind"] = "start_run", ["job_id"] = "j42", ["run_id"] = "r1" });

        var line = File.ReadAllText(path).TrimEnd('\n');
        var doc = JsonDocument.Parse(line);
        Assert.Equal("start_run", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal("j42", doc.RootElement.GetProperty("job_id").GetString());

        File.Delete(path);
    }
}
