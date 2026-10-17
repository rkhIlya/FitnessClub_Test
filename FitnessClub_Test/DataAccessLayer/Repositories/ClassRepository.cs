using Common.Database.Repositories.Generic;
using DataAccessLayer.Dto;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataAccessLayer.Repositories;

public class ClassRepository : GenericRepository<Class>, IClassRepository
{
    public ClassRepository(DbSet<Class> dbSet)
        : base(dbSet)
    {
    }

    public async Task<List<AnalyticsItemDto>> GetAnalyticsInfoAsync(DateOnly from, DateOnly to, long? roomId, CancellationToken ct = default)
    {
        var query = _dbSet.Include(x => x.Enrollments).Where(x => DateOnly.FromDateTime(x.StartsAt) >= from && DateOnly.FromDateTime(x.StartsAt) <= to && !x.IsCancelled);

        if (roomId.HasValue)
        {
            query = query.Where(c => c.RoomId == roomId.Value);
        }

        var items = await query
            .GroupBy(c => new { c.StartsAt.Date, c.RoomId })
            .Select(g => new AnalyticsItemDto
            {
                Date = DateOnly.FromDateTime(g.Key.Date),
                Room = new RoomDto
                {
                    Id = g.Key.RoomId,
                    Name = g.Max(c => c.Room.Name)
                },
                ClassesCount = g.Count(),
                TotalCapacity = g.Sum(c => c.Capacity),

                BookedSeats = g.Sum(c => c.Enrollments
                    .Where(e => e.CancelledAt == null)
                    .Sum(e => (int?)e.Seats)) ?? 0
            })
            .OrderBy(x => x.Date)
            .ThenBy(x => x.Room.Id)
            .ToListAsync(ct);

        return items;
    }

    public async Task<(List<Class> Classes, bool HasMore)> GetClassesForScheduleAsync(
        DateTime from,
        DateTime to,
        long? roomId,
        DateTime? cursorStartsAt,
        long? cursorId,
        int limit,
        CancellationToken ct)
    {
        var query = _dbSet
            .Include(c => c.Room)
            .AsNoTracking()
            .Where(c => c.StartsAt >= from && c.StartsAt < to && !c.IsCancelled);

        if (roomId.HasValue)
        {
            query = query.Where(c => c.RoomId == roomId.Value);
        }

        if (cursorStartsAt.HasValue && cursorId.HasValue)
        {
            query = query.Where(c => c.StartsAt > cursorStartsAt.Value ||
                (c.StartsAt == cursorStartsAt.Value && c.Id > cursorId.Value));
        }

        query = query.OrderBy(c => c.StartsAt).ThenBy(c => c.Id);

        var items = await query.Take(limit + 1).ToListAsync(ct);

        var hasMore = items.Count > limit;
        if (hasMore)
        {
            items.RemoveAt(limit);
        }

        return (items, hasMore);
    }
}
