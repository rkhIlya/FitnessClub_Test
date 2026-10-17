using Common.Caching;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using Domain.Exceptions;
using Domain.Interface;
using Domain.Request;
using Domain.Response;
using Microsoft.EntityFrameworkCore;

namespace Domain.Service;
public class EnrollmentService : IEnrollmentService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ICacheService _cache;

    private const string ActiveStatus = "active";
    private const string CancelledStatus = "cancelled";
    private const int HoursValueForCancelledEnrollment = 2;

    public EnrollmentService(IUnitOfWork unitOfWork, IDateTimeProvider dataTimeProvider, ICacheService cache)
    {
        _unitOfWork = unitOfWork;
        _dateTimeProvider = dataTimeProvider;
        _cache = cache;
    }

    public async Task<CreateEnrollmentResponse> CreateEnrollmentAsync(CreateEnrollmentRequest request, long userId, string idempotencyKey, CancellationToken ct)
    {
        var cacheKey = $"{userId}:{idempotencyKey}";
        var cached = await _cache.GetAsync<CreateEnrollmentResponse>(cacheKey, ct);
        if (cached != null)
        {
            return cached;
        }

        var currentClass = await _unitOfWork.Class.GetByIdAsync(request.ClassId.Value, ct);

        if (currentClass == null)
        {
            throw new NotFoundException(ExceptionNames.ClassIsNotExists);
        }

        var bookedSeats = await _unitOfWork.Enrollment.GeBookedSeatsAsync(request.ClassId.Value, ct);

        if (bookedSeats + request.Seats > currentClass.Capacity)
        {
            throw new ConflictException(ExceptionNames.CapacityExceeded);
        }

        var pass = await _unitOfWork.Pass.Query().FirstOrDefaultAsync(x => x.UserId == userId, ct);

        if (pass.RemainingVisits < request.Seats)
        {
            throw new ConflictException(ExceptionNames.PassExhausted);
        }

        var dateTimeNow = _dateTimeProvider.UtcNow;
        if (DateOnly.FromDateTime(dateTimeNow) > pass?.ValidUntil)
        {
            throw new ConflictException(ExceptionNames.PassExpired);
        }

        if (dateTimeNow >= currentClass.StartsAt)
        {
            throw new ConflictException(ExceptionNames.ClassStarted);
        }

        if (currentClass.IsCancelled)
        {
            throw new ConflictException(ExceptionNames.ClassCancelled);
        }

        var newEnrollment = new Enrollment()
        {
            ClassId = request.ClassId.Value,
            Seats = request.Seats.Value,
            CreatedAt = dateTimeNow,
            Status = ActiveStatus,
            UserId = userId,
        };

        var createdEnrollment = await _unitOfWork.Enrollment.CreateAsync(newEnrollment, ct);

        pass.RemainingVisits -= request.Seats.Value;
        _unitOfWork.Pass.Update(pass);

        await _unitOfWork.SaveChangesAsync(ct);

        var response = new CreateEnrollmentResponse()
        {
            Id = createdEnrollment.Id,
            ClassId = createdEnrollment.ClassId,
            CreatedAt = createdEnrollment.CreatedAt,
            Status = createdEnrollment.Status,
            Seats = createdEnrollment.Seats
        };

        await _cache.SetAsync(cacheKey, response, TimeSpan.FromHours(24), ct: ct);

        return response;
    }

    public async Task<GetEnrollmentResponse> ChangeEnrollmentAsync(ChangeEnrollmentRequest request, long userId, long enrollmentId, CancellationToken ct)
    {
        var currentEnrollment = await _unitOfWork.Enrollment.GetEnrollmentDetailsAsync(enrollmentId, ct);

        if (currentEnrollment == null || currentEnrollment.UserId != userId)
        {
            throw new NotFoundException(ExceptionNames.EnrollmentIsNotExists);
        }

        var currentClass = currentEnrollment.Class;
        var dateTimeNow = DateTime.UtcNow;

        if (currentEnrollment.CancelledAt.HasValue)
        {
            throw new ConflictException(ExceptionNames.EnrollmentCancelled);
        }

        if (currentClass.IsCancelled)
        {
            throw new ConflictException(ExceptionNames.ClassCancelled);
        }

        if (dateTimeNow >= currentClass.StartsAt)
        {
            throw new ConflictException(ExceptionNames.ClassStarted);
        }

        var bookedSeats = await _unitOfWork.Enrollment.GeBookedSeatsAsync(currentClass.Id, ct); ;
        var diffSeats = request.Seats.Value - currentEnrollment.Seats;

        if (diffSeats > 0 && bookedSeats + diffSeats > currentClass.Capacity)
        {
            throw new ConflictException(ExceptionNames.CapacityExceeded);
        }

        var pass = await _unitOfWork.Pass.GetByUserAsync(userId, ct);

        if (diffSeats > 0 && pass.RemainingVisits < diffSeats)
        {
            throw new ConflictException(ExceptionNames.PassExhausted);
        }

        if (diffSeats > 0 && DateOnly.FromDateTime(dateTimeNow) > pass.ValidUntil)
        {
            throw new ConflictException(ExceptionNames.PassExpired);
        }

        if (diffSeats != 0)
        {
            currentEnrollment.Seats = request.Seats.Value;
            _unitOfWork.Enrollment.Update(currentEnrollment);
            pass.RemainingVisits -= diffSeats;
            _unitOfWork.Pass.Update(pass);

            await _unitOfWork.SaveChangesAsync(ct);
        }

        await InvalidateCache(currentEnrollment.UserId, currentEnrollment.Id, ct);

        return new GetEnrollmentResponse()
        {
            Id = currentEnrollment.Id,
            Status = currentEnrollment.Status,
            Seats = currentEnrollment.Seats,
            CreatedAt = currentEnrollment.CreatedAt,
            Class = new EnrollmentClass()
            {
                Id = currentEnrollment.ClassId,
                Title = currentEnrollment.Class.Title,
                StartsAt = currentEnrollment.Class.StartsAt,
                DurationMinutes = currentEnrollment.Class.DurationMinutes,
                IsCanceled = currentEnrollment.Class.IsCancelled,
                Room = new EnrollmentRoom()
                {
                    Id = currentEnrollment.Class.RoomId,
                    Name = currentEnrollment.Class.Room.Name,
                },
            },
            Pass = new EnrollmentPass()
            {
                RemainingVisits = pass.RemainingVisits,
                ValidUntil = pass.ValidUntil
            }
        };
    }

    public async Task<CancelEnrollmentResponse> CancelEnrollmentAsync(long userId, long enrollmentId, CancellationToken ct)
    {
        var currentEnrollment = await _unitOfWork.Enrollment.GetEnrollmentWithClassAsync(enrollmentId, ct);

        if (currentEnrollment == null || currentEnrollment.UserId != userId)
        {
            throw new NotFoundException(ExceptionNames.EnrollmentIsNotExists);
        }

        if (currentEnrollment.Class.IsCancelled)
        {
            await GetCancelEnrollmentResponse(currentEnrollment, userId, currentEnrollment.Seats, ct);
        }

        if (currentEnrollment.Status.Equals(CancelledStatus, StringComparison.InvariantCultureIgnoreCase))
        {
            return new CancelEnrollmentResponse()
            {
                Id = currentEnrollment.Id,
                Status = CancelledStatus,
                RefundedVisits = currentEnrollment.Seats
            };
        }

        var dateTimeNow = _dateTimeProvider.UtcNow;
        if (dateTimeNow > currentEnrollment.Class.StartsAt)
        {
            throw new ConflictException(ExceptionNames.ClassStarted);
        }

        var hoursUntilStartClass = (currentEnrollment.Class.StartsAt - dateTimeNow).TotalHours;

        if (hoursUntilStartClass >= HoursValueForCancelledEnrollment)
        {
            return await GetCancelEnrollmentResponse(currentEnrollment, userId, currentEnrollment.Seats, ct);
        }
        else
        {
            return await GetCancelEnrollmentResponse(currentEnrollment, userId, 0, ct);
        }
    }

    public async Task<GetEnrollmentResponse> GetEnrollmentAsync(long userId, long enrollmentId, CancellationToken ct)
    {
        var cacheKey = $"userId={userId}:enrollmentId={enrollmentId}";
        var cached = await _cache.GetAsync<GetEnrollmentResponse>(cacheKey, ct);
        if (cached != null)
        {
            return cached;
        }

        var currentEnrollment = await _unitOfWork.Enrollment.GetEnrollmentDetailsAsync(enrollmentId, ct);

        if (currentEnrollment == null || currentEnrollment.UserId != userId)
        {
            throw new NotFoundException(ExceptionNames.EnrollmentIsNotExists);
        }

        var pass = await _unitOfWork.Pass.GetByUserAsync(userId, ct);

        var response = new GetEnrollmentResponse()
        {
            Id = currentEnrollment.Id,
            Status = currentEnrollment.Status,
            Seats = currentEnrollment.Seats,
            CreatedAt = currentEnrollment.CreatedAt,
            Class = new EnrollmentClass()
            {
                Id = currentEnrollment.ClassId,
                Title = currentEnrollment.Class.Title,
                StartsAt = currentEnrollment.Class.StartsAt,
                DurationMinutes = currentEnrollment.Class.DurationMinutes,
                IsCanceled = currentEnrollment.Class.IsCancelled,
                Room = new EnrollmentRoom()
                {
                    Id = currentEnrollment.Class.RoomId,
                    Name = currentEnrollment.Class.Room.Name,
                },
            },
            Pass = new EnrollmentPass()
            {
                RemainingVisits = pass.RemainingVisits,
                ValidUntil = pass.ValidUntil
            }
        };

        await _cache.SetAsync(cacheKey, response, TimeSpan.FromHours(24), ct: ct);

        return response;
    }

    public async Task<JoinWaitlistResponse> JoinWaitlistAsync(JoinWaitlistRequest request, long userId, string idempotencyKey, CancellationToken ct)
    {
        var cacheKey = $"{userId}:{idempotencyKey}";
        var cached = await _cache.GetAsync<JoinWaitlistResponse>(cacheKey, ct);
        if (cached != null)
        {
            return cached;
        }

        var currentClass = await _unitOfWork.Class.GetByIdAsync(request.ClassId.Value, ct);
        if (currentClass == null)
        {
            throw new NotFoundException(ExceptionNames.ClassIsNotExists);
        }

        if (currentClass.IsCancelled)
        {
            throw new ConflictException(ExceptionNames.ClassCancelled);
        }

        var dateTimeNow = _dateTimeProvider.UtcNow;
        if (currentClass.StartsAt <= dateTimeNow)
        {
            throw new ConflictException(ExceptionNames.ClassStarted);
        }

        var isAlreadyWaiting = await _unitOfWork.Waitlist.ExistsActiveAsync(userId, request.ClassId.Value, ct);
        if (isAlreadyWaiting)
        {
            throw new ConflictException(ExceptionNames.AlreadyWaiting);
        }

        var bookedSeats = await _unitOfWork.Enrollment.GeBookedSeatsAsync(request.ClassId.Value, ct);
        var freeSeats = currentClass.Capacity - bookedSeats;

        if (freeSeats >= request.Seats)
        {
            throw new ConflictException(ExceptionNames.SeatsAvailable);
        }

        var waitlist = new Waitlist
        {
            UserId = userId,
            ClassId = request.ClassId.Value,
            Seats = request.Seats.Value,
            CreatedAt = dateTimeNow,
            Status = ActiveStatus
        };

        await _unitOfWork.Waitlist.CreateAsync(waitlist, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        // решение: позиция в очереди равна общему количеству активных записей в листе ожидания 
        // для данного занятия. Так как мы только что добавили запись с самым поздним CreatedAt, 
        // её порядковый номер в отсортированном по времени списке равен общему количеству.
        var position = await _unitOfWork.Waitlist.GetActiveCountAsync(request.ClassId.Value, ct);

        var response = new JoinWaitlistResponse()
        {
            Id = waitlist.Id,
            ClassId = waitlist.ClassId,
            Seats = waitlist.Seats,
            Position = position,
            CreatedAt = waitlist.CreatedAt
        };

        await _cache.SetAsync(cacheKey, response, TimeSpan.FromHours(24), ct: ct);

        return response;
    }

    public async Task<CancelWaitlistResponse> CancelWaitlistAsync(long userId, long waitlistId, CancellationToken ct)
    {
        var waitList = await _unitOfWork.Waitlist.GetByIdAsync(waitlistId, ct);

        if (waitList == null || waitList.UserId != userId)
        {
            throw new NotFoundException(ExceptionNames.WaitlistIsNotExists);
        }

        if (waitList.Status == CancelledStatus)
        {
            return new CancelWaitlistResponse()
            {
                Id = waitList.Id,
                Status = CancelledStatus
            };
        }

        waitList.Status = CancelledStatus;
        _unitOfWork.Waitlist.Update(waitList);
        await _unitOfWork.SaveChangesAsync(ct);

        return new CancelWaitlistResponse()
        {
            Id = waitList.Id,
            Status = CancelledStatus
        };
    }

    private async Task<CancelEnrollmentResponse> GetCancelEnrollmentResponse(Enrollment enrollment, long userId, int refundedVisits, CancellationToken ct)
    {
        var pass = await _unitOfWork.Pass.GetByUserAsync(userId, ct);
        pass.RemainingVisits += refundedVisits;
        _unitOfWork.Pass.Update(pass);

        enrollment.Status = CancelledStatus;
        enrollment.CancelledAt = _dateTimeProvider.UtcNow;
        _unitOfWork.Enrollment.Update(enrollment);

        if (!enrollment.Class.IsCancelled)
        {
            await PromoteFromWaitlistAsync(enrollment.ClassId, enrollment.Seats, ct);
        }
        
        await _unitOfWork.SaveChangesAsync(ct);

        await InvalidateCache(enrollment.UserId, enrollment.Id, ct);

        return new CancelEnrollmentResponse()
        {
            Id = enrollment.Id,
            Status = CancelledStatus,
            RefundedVisits = refundedVisits
        };
    }

    private async Task PromoteFromWaitlistAsync(long classId, int freedSeats, CancellationToken ct)
    {
        var currentClass = await _unitOfWork.Class.GetByIdAsync(classId, ct);
        var bookedSeats = await _unitOfWork.Enrollment.GeBookedSeatsAsync(classId, ct);
        var freeSeats = currentClass.Capacity - bookedSeats + freedSeats;
        var dateTimeNow = _dateTimeProvider.UtcNow;

        if (freeSeats <= 0)
        {
            return;
        }

        var waitlists = await _unitOfWork.Waitlist.GetOrderedByClassAsync(classId, ct);

        foreach (var waitlist in waitlists)
        {
            if (freeSeats < waitlist.Seats)
            {
                continue;
            }

            var pass = await _unitOfWork.Pass.GetByUserAsync(waitlist.UserId, ct);

            if (pass.ValidUntil < DateOnly.FromDateTime(dateTimeNow) || pass.RemainingVisits < waitlist.Seats)
            {
                waitlist.Status = CancelledStatus;
                continue;
            }

            var newEnrollment = new Enrollment
            {
                UserId = waitlist.UserId,
                ClassId = classId,
                Seats = waitlist.Seats,
                Status = ActiveStatus,
                CreatedAt = dateTimeNow
            };

            pass.RemainingVisits -= waitlist.Seats;
            freeSeats -= waitlist.Seats;
            waitlist.Status = CancelledStatus;

            await _unitOfWork.Enrollment.CreateAsync(newEnrollment, ct);
            _unitOfWork.Pass.Update(pass);
            _unitOfWork.Waitlist.Update(waitlist);
        }
    }

    private async Task InvalidateCache(long userId, long enrollmentId, CancellationToken ct)
    {
        var cacheKey = $"userId={userId}:enrollmentId={enrollmentId}";
        await _cache.RemoveAsync(cacheKey, ct: ct);
    }
}
