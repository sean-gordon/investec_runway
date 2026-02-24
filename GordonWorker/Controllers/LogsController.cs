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

    public LogsController(ILogSinkService sink)
    {
        _sink = sink;
    }

    [HttpGet]
    public IActionResult GetLogs()
    {
        // Allowed for all authenticated users per user request
        return Ok(_sink.GetLogs());
    }
}
