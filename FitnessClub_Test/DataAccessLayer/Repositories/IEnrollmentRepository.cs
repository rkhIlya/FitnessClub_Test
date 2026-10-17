using Common.Database.Repositories.Generic;
using DataAccessLayer.Dto;
using DataAccessLayer.Entities;

namespace DataAccessLayer.Repositories;

public interface IEnrollmentRepository : IGenericRepository<Enrollment>
{
    Task<List<Enrollment>> GetByUserAsync(long userId, CancellationToken ct = default);

    Task<int> GeBookedSeatsAsync(long classId, CancellationToken ct = default);

    Task<Enrollment> GetEnrollmentDetailsAsync(long id, CancellationToken ct = default);

    Task<Enrollment> GetEnrollmentWithClassAsync(long id, CancellationToken ct = default);

    Task<IEnumerable<ClassEnrollmentDto>> GetEnrollmentSummariesForClassesAsync(List<long> classIds, long userId, CancellationToken ct);
}
