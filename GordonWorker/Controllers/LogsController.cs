using GordonWorker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GordonWorker.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class LogsController : ControllerBase
{
    private readonly ILogSinkService _sink;

    private static readonly string LogRoot = Path.Combine(
        AppContext.BaseDirectory.Contains("/app") ? "/app" : AppContext.BaseDirectory,
        "logs");

    public LogsController(ILogSinkService sink)
    {
        _sink = sink;
    }

    /// <summary>Returns the last 1000 log entries from memory (real-time, existing Logs tab feed).</summary>
    [HttpGet]
    public IActionResult GetLogs()
    {
        return Ok(_sink.GetLogs());
    }

    /// <summary>Lists available log files grouped by level (info/error/debug) with dates.</summary>
    [HttpGet("files")]
    public IActionResult ListLogFiles()
    {
        var result = new Dictionary<string, List<string>>();
        foreach (var level in new[] { "info", "error", "debug" })
        {
            var dir = Path.Combine(LogRoot, level);
            if (!Directory.Exists(dir)) { result[level] = new(); continue; }
            result[level] = Directory.GetFiles(dir, "gordon-*.log")
                .Select(Path.GetFileNameWithoutExtension)
                .Select(f => f!.Replace("gordon-", ""))
                .OrderByDescending(d => d)
                .ToList();
        }
        return Ok(result);
    }

    /// <summary>Returns the content of a specific log file. level = info|error|debug, date = YYYY-MM-DD.</summary>
    [HttpGet("files/{level}/{date}")]
    public IActionResult GetLogFile(string level, string date)
    {
        if (!new[] { "info", "error", "debug" }.Contains(level.ToLower()))
            return BadRequest("Invalid level. Use: info, error, debug");

        // Basic path traversal guard
        if (!System.Text.RegularExpressions.Regex.IsMatch(date, @"^\d{4}-\d{2}-\d{2}$"))
            return BadRequest("Invalid date format. Use YYYY-MM-DD");

        var filePath = Path.Combine(LogRoot, level.ToLower(), $"gordon-{date}.log");
        if (!System.IO.File.Exists(filePath))
            return NotFound($"No {level} log found for {date}");

        var content = System.IO.File.ReadAllText(filePath);
        return Content(content, "text/plain");
    }

    /// <summary>Returns the tail (last N lines) of a log file. Defaults to 200 lines.</summary>
    [HttpGet("files/{level}/{date}/tail")]
    public IActionResult TailLogFile(string level, string date, [FromQuery] int lines = 200)
    {
        if (!new[] { "info", "error", "debug" }.Contains(level.ToLower()))
            return BadRequest("Invalid level.");

        if (!System.Text.RegularExpressions.Regex.IsMatch(date, @"^\d{4}-\d{2}-\d{2}$"))
            return BadRequest("Invalid date format.");

        var filePath = Path.Combine(LogRoot, level.ToLower(), $"gordon-{date}.log");
        if (!System.IO.File.Exists(filePath))
            return NotFound($"No {level} log found for {date}");

        var allLines = System.IO.File.ReadAllLines(filePath);
        var tail = allLines.TakeLast(Math.Clamp(lines, 1, 5000));
        return Content(string.Join(Environment.NewLine, tail), "text/plain");
    }
}
