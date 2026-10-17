using Domain.Request;
using Domain.Response;

namespace Domain.Interface
{
    public interface IEnrollmentService
    {
        Task<CreateEnrollmentResponse> CreateEnrollmentAsync(CreateEnrollmentRequest request, long userId, string idempotencyKey, CancellationToken ct);

        Task<GetEnrollmentResponse> ChangeEnrollmentAsync(ChangeEnrollmentRequest request, long userId, long enrollmentId, CancellationToken ct);

        Task<CancelEnrollmentResponse> CancelEnrollmentAsync(long userId, long enrollmentId, CancellationToken ct);

        Task<GetEnrollmentResponse> GetEnrollmentAsync(long userId, long enrollmentId, CancellationToken ct);

        Task<JoinWaitlistResponse> JoinWaitlistAsync(JoinWaitlistRequest request, long userId, string idempotencyKey, CancellationToken ct);

        Task<CancelWaitlistResponse> CancelWaitlistAsync(long userId, long waitlistId, CancellationToken ct);
    }
}
