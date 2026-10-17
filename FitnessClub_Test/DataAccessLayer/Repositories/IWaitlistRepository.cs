using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;

namespace DataAccessLayer.Repositories;
public interface IWaitlistRepository : IGenericRepository<Waitlist>
{
    Task<bool> ExistsActiveAsync(long userId, long classId, CancellationToken ct);

    Task<int> GetActiveCountAsync(long classId, CancellationToken ct);

    Task<List<Waitlist>> GetOrderedByClassAsync(long classId, CancellationToken ct);
}
