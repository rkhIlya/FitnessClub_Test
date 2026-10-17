using Common.Database.Repositories.Generic;
using DataAccessLayer.Dto;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataAccessLayer.Repositories;

public class EnrollmentRepository : GenericRepository<Enrollment>, IEnrollmentRepository
{
    private const string ActiveStatus = "active";

    public EnrollmentRepository(DbSet<Enrollment> dbSet)
        : base(dbSet)
    {
    }

    public async Task<List<Enrollment>> GetByUserAsync(long userId, CancellationToken ct = default)
    {
        return await _dbSet
            .Where(x => x.UserId == userId)
            .ToListAsync(ct);
    }

    public async Task<int> GeBookedSeatsAsync(long classId, CancellationToken ct = default)
    {
        return await _dbSet
            .Where(e => e.ClassId == classId && e.Status == ActiveStatus)
            .SumAsync(e => e.Seats, ct);
    }

    public async Task<Enrollment> GetEnrollmentDetailsAsync(long id, CancellationToken ct = default)
    {
        return await _dbSet
            .Include(x => x.Class)
            .ThenInclude(x => x.Room)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken: ct);
    }

    public async Task<Enrollment> GetEnrollmentWithClassAsync(long id, CancellationToken ct = default)
    {
        return await _dbSet
            .Include(x => x.Class)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken: ct);
    }

    public async Task<IEnumerable<ClassEnrollmentDto>> GetEnrollmentSummariesForClassesAsync(List<long> classIds, long userId, CancellationToken ct)
    {
        if (!classIds.Any())
        {
            return Array.Empty<ClassEnrollmentDto>();
        }

        return await _dbSet
            .AsNoTracking()
            .Where(e => classIds.Contains(e.ClassId) && e.Status == ActiveStatus)
            .GroupBy(e => e.ClassId)
            .Select(g => new ClassEnrollmentDto()
            {
                ClassId = g.Key,
                TotalBookedSeats = g.Sum(e => e.Seats),
                UserBookedSeats = g.Where(e => e.UserId == userId).Sum(e => e.Seats)
            })
            .ToListAsync(ct);
    }
}
