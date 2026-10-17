using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataAccessLayer.Repositories;
public class WaitlistRepository : GenericRepository<Waitlist>, IWaitlistRepository
{
    private const string ActiveStatus = "active";

    public WaitlistRepository(DbSet<Waitlist> dbSet) : base(dbSet)
    {
    }

    public async Task<bool> ExistsActiveAsync(long userId, long classId, CancellationToken ct)
    {
        return await _dbSet.AnyAsync(w => w.UserId == userId && w.ClassId == classId && w.Status == ActiveStatus, ct);
    }

    public async Task<int> GetActiveCountAsync(long classId, CancellationToken ct)
    {
        return await _dbSet.CountAsync(w => w.ClassId == classId && w.Status == ActiveStatus, ct);
    }

    public async Task<List<Waitlist>> GetOrderedByClassAsync(long classId, CancellationToken ct)
    {
        return await _dbSet
            .Where(w => w.ClassId == classId && w.Status == ActiveStatus)
            .OrderBy(w => w.CreatedAt)
            .ToListAsync(ct);
    }
}
