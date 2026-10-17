using Domain.Interface;
using Domain.Request;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace WebApi.Controllers;

[ApiController]
[Authorize]
[Route("analytics")]
public class AnalyticsController : BaseApiController
{
    [HttpGet]
    [Route("load")]
    public async Task<IActionResult> LoadAnalytics(
        [FromQuery] LoadAnalyticsRequest request,
        [FromServices] IAnalyticsService analyticsService,
        CancellationToken ct)
    {
        if (!IsAdmin)
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var result = await analyticsService.LoadAnalyticsAsync(request, ct);

        return Ok(result);
    }
}
