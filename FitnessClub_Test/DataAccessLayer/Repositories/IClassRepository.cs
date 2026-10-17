using Common.Database.Repositories.Generic;
using DataAccessLayer.Dto;
using DataAccessLayer.Entities;

namespace DataAccessLayer.Repositories;

public interface IClassRepository : IGenericRepository<Class>
{
    Task<List<AnalyticsItemDto>> GetAnalyticsInfoAsync(DateOnly from, DateOnly to, long? roomId, CancellationToken ct = default);

    Task<(List<Class> Classes, bool HasMore)> GetClassesForScheduleAsync(
        DateTime from,
        DateTime to,
        long? roomId,
        DateTime? cursorStartsAt,
        long? cursorId,
        int limit,
        CancellationToken ct);
}
