using GordonWorker.Services;
using Microsoft.AspNetCore.Mvc;

namespace GordonWorker.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SettingsController : ControllerBase
{
    private readonly ISettingsService _settingsService;

    public SettingsController(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var settings = await _settingsService.GetSettingsAsync();
        return Ok(settings);
    }

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] AppSettings settings)
    {
        await _settingsService.UpdateSettingsAsync(settings);
        return Ok(settings);
    }
}
