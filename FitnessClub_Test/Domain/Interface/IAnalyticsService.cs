using Domain.Request;
using Domain.Response;

namespace Domain.Interface;
public interface IAnalyticsService
{
    Task<LoadAnalyticsResponse> LoadAnalyticsAsync(LoadAnalyticsRequest request, CancellationToken ct);
}
