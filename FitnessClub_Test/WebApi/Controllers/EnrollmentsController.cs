using System.ComponentModel.DataAnnotations;
using Domain.Interface;
using Domain.Request;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace WebApi.Controllers;

[ApiController]
[Authorize]
[Route("enrollments")]
public class EnrollmentsController : BaseApiController
{
    [HttpPost]
    public async Task<IActionResult> CreateEnrollment(
        [FromBody] CreateEnrollmentRequest request,
        [FromServices] IEnrollmentService enrollmentService,
        [FromHeader(Name = RequestHeaders.IdempotencyKey)][Required(AllowEmptyStrings = false)] string idempotencyKey,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var result = await enrollmentService.CreateEnrollmentAsync(request, CurrentUserId, idempotencyKey, ct);

        return Created("", result);
    }

    [HttpPost]
    [Route("{enrollmentId}/seats")]
    public async Task<IActionResult> ChangeEnrollment(
    [FromBody] ChangeEnrollmentRequest request,
    [FromRoute] long enrollmentId,
    [FromServices] IEnrollmentService enrollmentService,
    CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest();
        }

        var result = await enrollmentService.ChangeEnrollmentAsync(request, CurrentUserId, enrollmentId, ct);

        return Ok(result);
    }

    [HttpPost]
    [Route("{enrollmentId}/cancel")]
    public async Task<IActionResult> CancelEnrollment(
        [FromRoute] long enrollmentId,
        [FromServices] IEnrollmentService enrollmentService,
        CancellationToken ct)
    {
        var result = await enrollmentService.CancelEnrollmentAsync(CurrentUserId, enrollmentId, ct);

        return Ok(result);
    }

    [HttpGet]
    [Route("{enrollmentId}")]
    public async Task<IActionResult> GetEnrollment(
    [FromRoute] long enrollmentId,
    [FromServices] IEnrollmentService enrollmentService,
    CancellationToken ct)
    {
        var result = await enrollmentService.GetEnrollmentAsync(CurrentUserId, enrollmentId, ct);

        return Ok(result);
    }

    [HttpPost]
    [Route("waitlist")]
    public async Task<IActionResult> JoinWaitlist(
        [FromBody] JoinWaitlistRequest joinWaitlistRequest,
        [FromServices] IEnrollmentService enrollmentService,
        [FromHeader(Name = RequestHeaders.IdempotencyKey)][Required(AllowEmptyStrings = false)] string idempotencyKey,
        CancellationToken ct)
    {
        var result = await enrollmentService.JoinWaitlistAsync(joinWaitlistRequest, CurrentUserId, idempotencyKey, ct);

        return Created("", result);
    }

    [HttpPost]
    [Route("waitlist/{waitlistId}/cancel")]
    public async Task<IActionResult> CancelWaitlist(
        [FromRoute] long waitlistId,
        [FromServices] IEnrollmentService enrollmentService,
        CancellationToken ct)
    {
        var result = await enrollmentService.CancelWaitlistAsync(CurrentUserId, waitlistId, ct);

        return Ok(result);
    }
}
