using Domain.Request;
using Domain.Response;

namespace Domain.Interface;
public interface IScheduleService
{
    Task<GetScheduleResponse> GetScheduleAsync(GetScheduleRequest request, long userId, CancellationToken ct);
}
