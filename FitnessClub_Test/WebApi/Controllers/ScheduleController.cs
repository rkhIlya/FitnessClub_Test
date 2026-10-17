using Domain.Interface;
using Domain.Request;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace WebApi.Controllers;

[ApiController]
[Authorize]
[Route("schedule")]
public class ScheduleController : BaseApiController
{
    [HttpGet]
    public async Task<IActionResult> GetSchedule(
        [FromQuery] GetScheduleRequest getScheduleRequest,
        [FromServices] IScheduleService scheduleService,
        CancellationToken ct)
    {
        var result = await scheduleService.GetScheduleAsync(getScheduleRequest, CurrentUserId, ct);

        return Ok(result);
    }
}
