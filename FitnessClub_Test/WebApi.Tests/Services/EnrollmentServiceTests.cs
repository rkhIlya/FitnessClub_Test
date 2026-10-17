using Common.Caching;
using DataAccessLayer;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using Domain.Exceptions;
using Domain.Interface;
using Domain.Request;
using Domain.Response;
using Domain.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace WebApi.Tests.Services;

[TestClass]
public class EnrollmentServiceTests
{
    private IServiceProvider _provider;
    private ClubDbContext _dbContext;
    private IEnrollmentService _service;
    private ICacheService _cacheService;

    [TestInitialize]
    public async Task Setup()
    {
        var services = new ServiceCollection();

        services.AddDbContext<ClubDbContext>(o =>
            o.UseNpgsql(TestDatabase.PostgresConnectionString));

        services.Configure<RedisCacheSettings>(settings =>
        {
            settings.DefaultExpiration = TimeSpan.FromHours(1);
        });
        services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(TestDatabase.RedisConnectionString));
        services.AddSingleton<IDateTimeProvider, DateTimeProvider>();
        services.AddScoped<ICacheService, RedisCacheService>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IEnrollmentService, EnrollmentService>();

        _provider = services.BuildServiceProvider();

        var scope = _provider.CreateScope();
        _dbContext = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        _cacheService = scope.ServiceProvider.GetRequiredService<ICacheService>();
        var dateTimeProvider = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        _service = new EnrollmentService(unitOfWork, dateTimeProvider, _cacheService);
    }

    [TestMethod]
    public async Task CreateEnrollment_ValidData_ShouldCreateEnrollmentAndDecreasePassVisits()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user002@club.test");
        var pass = await _dbContext.Passes.FirstAsync(p => p.UserId == user.Id);
        var initialVisits = pass.RemainingVisits;

        var targetClass = await _dbContext.Classes
            .Where(c => c.StartsAt > DateTime.UtcNow && !c.IsCancelled)
            .OrderByDescending(c => c.StartsAt)
            .FirstAsync();

        var request = new CreateEnrollmentRequest { ClassId = targetClass.Id, Seats = 1 };
        var idempotencyKey = Guid.NewGuid().ToString();

        var result = await _service.CreateEnrollmentAsync(request, user.Id, idempotencyKey, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(targetClass.Id, result.ClassId);
        Assert.AreEqual(1, result.Seats);

        var enrollment = await _dbContext.Enrollments.FirstOrDefaultAsync(e => e.Id == result.Id);
        Assert.IsNotNull(enrollment);
        Assert.AreEqual("active", enrollment.Status);

        await _dbContext.Entry(pass).ReloadAsync();
        Assert.AreEqual(initialVisits - 1, pass.RemainingVisits);

        var cacheKey = $"{user.Id}:{idempotencyKey}";
        var cached = await _cacheService.GetAsync<CreateEnrollmentResponse>(cacheKey, CancellationToken.None);
        Assert.IsNotNull(cached);
        Assert.AreEqual(result.Id, cached.Id);
    }

    [TestMethod]
    public async Task CreateEnrollment_Idempotency_ShouldReturnCachedResponse()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user004@club.test");
        var targetClass = await _dbContext.Classes
            .Where(c => c.StartsAt > DateTime.UtcNow && !c.IsCancelled)
            .OrderByDescending(c => c.StartsAt)
            .FirstAsync();

        var request = new CreateEnrollmentRequest { ClassId = targetClass.Id, Seats = 1 };
        var idempotencyKey = Guid.NewGuid().ToString();

        var result1 = await _service.CreateEnrollmentAsync(request, user.Id, idempotencyKey, CancellationToken.None);

        var result2 = await _service.CreateEnrollmentAsync(request, user.Id, idempotencyKey, CancellationToken.None);

        Assert.AreEqual(result1.Id, result2.Id);

        var enrollmentsCount = await _dbContext.Enrollments
            .CountAsync(e => e.UserId == user.Id && e.ClassId == targetClass.Id);

        Assert.AreEqual(1, enrollmentsCount);
    }

    [TestMethod]
    public async Task CreateEnrollment_ClassNotFound_ShouldThrowNotFoundException()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user002@club.test");
        var request = new CreateEnrollmentRequest { ClassId = 999999, Seats = 1 };

        await Assert.ThrowsExceptionAsync<NotFoundException>(() =>
            _service.CreateEnrollmentAsync(request, user.Id, Guid.NewGuid().ToString(), CancellationToken.None));
    }

    [TestMethod]
    public async Task CreateEnrollment_CapacityExceeded_ShouldThrowConflictException()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user003@club.test");
        var targetClass = await _dbContext.Classes
            .Where(c => c.StartsAt > DateTime.UtcNow && !c.IsCancelled)
            .OrderByDescending(c => c.StartsAt)
            .FirstAsync();

        var request = new CreateEnrollmentRequest { ClassId = targetClass.Id, Seats = 100 };

        var ex = await Assert.ThrowsExceptionAsync<ConflictException>(() =>
            _service.CreateEnrollmentAsync(request, user.Id, Guid.NewGuid().ToString(), CancellationToken.None));

        Assert.AreEqual(ExceptionNames.CapacityExceeded, ex.Reason);
    }

    [TestMethod]
    public async Task CreateEnrollment_PassExhausted_ShouldThrowConflictException()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user020@club.test");
        var targetClass = await _dbContext.Classes
            .Where(c => c.StartsAt > DateTime.UtcNow && !c.IsCancelled)
            .OrderByDescending(c => c.StartsAt)
            .FirstAsync();

        var request = new CreateEnrollmentRequest { ClassId = targetClass.Id, Seats = 1 };

        var ex = await Assert.ThrowsExceptionAsync<ConflictException>(() =>
            _service.CreateEnrollmentAsync(request, user.Id, Guid.NewGuid().ToString(), CancellationToken.None));

        Assert.AreEqual(ExceptionNames.PassExhausted, ex.Reason);
    }

    [TestMethod]
    public async Task CreateEnrollment_PassExpired_ShouldThrowConflictException()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user017@club.test");
        var targetClass = await _dbContext.Classes
            .Where(c => c.StartsAt > DateTime.UtcNow && !c.IsCancelled)
            .OrderByDescending(c => c.StartsAt)
            .FirstAsync();

        var request = new CreateEnrollmentRequest { ClassId = targetClass.Id, Seats = 1 };

        var ex = await Assert.ThrowsExceptionAsync<ConflictException>(() =>
            _service.CreateEnrollmentAsync(request, user.Id, Guid.NewGuid().ToString(), CancellationToken.None));

        Assert.AreEqual(ExceptionNames.PassExpired, ex.Reason);
    }

    [TestMethod]
    public async Task CreateEnrollment_ClassStarted_ShouldThrowConflictException()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user002@club.test");
        var targetClass = await _dbContext.Classes
            .Where(c => c.StartsAt <= DateTime.UtcNow)
            .OrderBy(c => c.StartsAt)
            .FirstAsync();

        var request = new CreateEnrollmentRequest { ClassId = targetClass.Id, Seats = 1 };

        var ex = await Assert.ThrowsExceptionAsync<ConflictException>(() =>
            _service.CreateEnrollmentAsync(request, user.Id, Guid.NewGuid().ToString(), CancellationToken.None));

        Assert.AreEqual(ExceptionNames.ClassStarted, ex.Reason);
    }

    [TestMethod]
    public async Task CreateEnrollment_ClassCancelled_ShouldThrowConflictException()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user002@club.test");
        var targetClass = await _dbContext.Classes.FirstAsync(c => c.IsCancelled && c.StartsAt > DateTime.UtcNow);

        var request = new CreateEnrollmentRequest { ClassId = targetClass.Id, Seats = 1 };

        var ex = await Assert.ThrowsExceptionAsync<ConflictException>(() =>
            _service.CreateEnrollmentAsync(request, user.Id, Guid.NewGuid().ToString(), CancellationToken.None));

        Assert.AreEqual(ExceptionNames.ClassCancelled, ex.Reason);
    }

    [TestMethod]
    public async Task ChangeEnrollment_IncreaseSeats_Success()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user006@club.test");
        var enrollment = await _dbContext.Enrollments
            .Include(e => e.Class).ThenInclude(c => c.Room)
            .FirstAsync(e => e.UserId == user.Id && e.CancelledAt == null && e.Class.StartsAt > DateTime.UtcNow && !e.Class.IsCancelled);

        var pass = await _dbContext.Passes.FirstAsync(p => p.UserId == user.Id);
        var initialVisits = pass.RemainingVisits;
        var initialSeats = enrollment.Seats;

        var request = new ChangeEnrollmentRequest { Seats = initialSeats + 1 };

        var result = await _service.ChangeEnrollmentAsync(request, user.Id, enrollment.Id, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(enrollment.Id, result.Id);
        Assert.AreEqual(initialSeats + 1, result.Seats);
        Assert.AreEqual(initialVisits - 1, result.Pass.RemainingVisits);
        Assert.IsNotNull(result.Class);
        Assert.AreEqual(enrollment.ClassId, result.Class.Id);

        await _dbContext.Entry(enrollment).ReloadAsync();
        Assert.AreEqual(initialSeats + 1, enrollment.Seats);

        await _dbContext.Entry(pass).ReloadAsync();
        Assert.AreEqual(initialVisits - 1, pass.RemainingVisits);
    }

    [TestMethod]
    public async Task ChangeEnrollment_DecreaseSeats_Success()
    {
        var enrollment = await _dbContext.Enrollments
            .Include(e => e.Class).ThenInclude(c => c.Room)
            .FirstAsync(e => e.CancelledAt == null && e.Seats >= 2 && e.Class.StartsAt > DateTime.UtcNow && !e.Class.IsCancelled);
        var enrollmentSeats = enrollment.Seats;
        var user = await _dbContext.Users.FirstAsync(u => u.Id == enrollment.UserId);
        var pass = await _dbContext.Passes.FirstAsync(p => p.UserId == user.Id);
        var initialVisits = pass.RemainingVisits;

        var request = new ChangeEnrollmentRequest { Seats = enrollmentSeats - 1 };

        var result = await _service.ChangeEnrollmentAsync(request, user.Id, enrollment.Id, CancellationToken.None);

        Assert.AreEqual(enrollmentSeats - 1, result.Seats);
        Assert.AreEqual(initialVisits + 1, result.Pass.RemainingVisits);
    }

    [TestMethod]
    public async Task ChangeEnrollment_NoChangeInSeats_Success()
    {
        var enrollment = await _dbContext.Enrollments
            .FirstAsync(e => e.CancelledAt == null && e.Class.StartsAt > DateTime.UtcNow && !e.Class.IsCancelled);

        var request = new ChangeEnrollmentRequest { Seats = enrollment.Seats };

        var result = await _service.ChangeEnrollmentAsync(request, enrollment.UserId, enrollment.Id, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(enrollment.Seats, result.Seats);
    }

    [TestMethod]
    public async Task ChangeEnrollment_EnrollmentNotFound_ThrowsNotFoundException()
    {
        var user = await _dbContext.Users.FirstAsync();
        var request = new ChangeEnrollmentRequest { Seats = 1 };

        await Assert.ThrowsExceptionAsync<NotFoundException>(() =>
            _service.ChangeEnrollmentAsync(request, user.Id, 999999, CancellationToken.None));
    }

    [TestMethod]
    public async Task ChangeEnrollment_WrongUserId_ThrowsNotFoundException()
    {
        var enrollment = await _dbContext.Enrollments.FirstAsync(e => e.CancelledAt == null);
        var anotherUser = await _dbContext.Users.FirstAsync(u => u.Id != enrollment.UserId);

        var request = new ChangeEnrollmentRequest { Seats = enrollment.Seats };

        await Assert.ThrowsExceptionAsync<NotFoundException>(() =>
            _service.ChangeEnrollmentAsync(request, anotherUser.Id, enrollment.Id, CancellationToken.None));
    }

    [TestMethod]
    public async Task ChangeEnrollment_ClassStarted_ThrowsConflictException()
    {
        var enrollment = await _dbContext.Enrollments
            .Include(e => e.Class)
            .FirstAsync(e => e.CancelledAt == null && e.Class.StartsAt <= DateTime.UtcNow);

        var request = new ChangeEnrollmentRequest { Seats = enrollment.Seats };

        var ex = await Assert.ThrowsExceptionAsync<ConflictException>(() =>
            _service.ChangeEnrollmentAsync(request, enrollment.UserId, enrollment.Id, CancellationToken.None));

        Assert.AreEqual(ExceptionNames.ClassStarted, ex.Reason);
    }

    [TestMethod]
    public async Task ChangeEnrollment_ClassCancelled_ThrowsConflictException()
    {
        var enrollment = await _dbContext.Enrollments
            .Include(e => e.Class)
            .FirstAsync(e => e.CancelledAt == null && e.Class.StartsAt > DateTime.UtcNow && !e.Class.IsCancelled);

        enrollment.Class.IsCancelled = true;
        await _dbContext.SaveChangesAsync();

        var request = new ChangeEnrollmentRequest { Seats = enrollment.Seats };

        var ex = await Assert.ThrowsExceptionAsync<ConflictException>(() =>
            _service.ChangeEnrollmentAsync(request, enrollment.UserId, enrollment.Id, CancellationToken.None));

        Assert.AreEqual(ExceptionNames.ClassCancelled, ex.Reason);
    }

    [TestMethod]
    public async Task ChangeEnrollment_EnrollmentAlreadyCancelled_ThrowsConflictException()
    {
        var enrollment = await _dbContext.Enrollments.FirstAsync(e => e.CancelledAt != null);

        var request = new ChangeEnrollmentRequest { Seats = enrollment.Seats };

        var ex = await Assert.ThrowsExceptionAsync<ConflictException>(() =>
            _service.ChangeEnrollmentAsync(request, enrollment.UserId, enrollment.Id, CancellationToken.None));

        Assert.AreEqual(ExceptionNames.EnrollmentCancelled, ex.Reason);
    }

    [TestMethod]
    public async Task ChangeEnrollment_CapacityExceeded_ThrowsConflictException()
    {
        var targetClass = await _dbContext.Classes
            .Include(c => c.Enrollments)
            .Where(c => !c.IsCancelled && c.StartsAt > DateTime.UtcNow)
            .Select(c => new
            {
                Class = c,
                TakenSeats = c.Enrollments
                    .Where(e => e.Status == "active" && e.CancelledAt == null)
                    .Sum(e => e.Seats),
                ActiveEnrollments = c.Enrollments
                    .Where(e => e.Status == "active" && e.CancelledAt == null)
                    .ToList()
            })
            .Where(x => x.TakenSeats > 0 && x.TakenSeats < x.Class.Capacity)
            .OrderBy(x => x.Class.Capacity - x.TakenSeats)
            .FirstOrDefaultAsync();

        var enrollment = targetClass.ActiveEnrollments.First();
        var user = await _dbContext.Users.FirstAsync(u => u.Id == enrollment.UserId);

        var pass = await _dbContext.Passes.FirstAsync(p => p.UserId == user.Id);
        var availableSeats = targetClass.Class.Capacity - targetClass.TakenSeats;
        var requestedSeats = enrollment.Seats + availableSeats + 1;

        var request = new ChangeEnrollmentRequest { Seats = requestedSeats };

        var exception = await Assert.ThrowsExceptionAsync<ConflictException>(() =>
            _service.ChangeEnrollmentAsync(request, user.Id, enrollment.Id, CancellationToken.None));

        Assert.AreEqual(ExceptionNames.CapacityExceeded, exception.Reason);

        await _dbContext.Entry(enrollment).ReloadAsync();
        Assert.AreEqual(enrollment.Seats, enrollment.Seats);

        await _dbContext.Entry(pass).ReloadAsync();
        Assert.AreEqual(pass.RemainingVisits, pass.RemainingVisits);
    }

    [TestMethod]
    public async Task ChangeEnrollment_PassExhausted_ThrowsConflictException()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user110@club.test");

        var enrollment = await _dbContext.Enrollments.FirstOrDefaultAsync(e => e.UserId == user.Id && e.CancelledAt == null);
        var request = new ChangeEnrollmentRequest { Seats = enrollment.Seats + 1 };

        var ex = await Assert.ThrowsExceptionAsync<ConflictException>(() =>
            _service.ChangeEnrollmentAsync(request, user.Id, enrollment.Id, CancellationToken.None));

        Assert.AreEqual(ExceptionNames.PassExhausted, ex.Reason);
    }

    [TestMethod]
    public async Task ChangeEnrollment_PassExpired_ThrowsConflictException()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user017@club.test");

        var enrollment = await _dbContext.Enrollments.FirstOrDefaultAsync(e => e.UserId == user.Id && e.CancelledAt == null);
        var request = new ChangeEnrollmentRequest { Seats = enrollment.Seats + 1 };

        var ex = await Assert.ThrowsExceptionAsync<ConflictException>(() =>
            _service.ChangeEnrollmentAsync(request, user.Id, enrollment.Id, CancellationToken.None));

        Assert.AreEqual(ExceptionNames.PassExpired, ex.Reason);
    }

    [TestMethod]
    public async Task CancelEnrollment_MoreThan2Hours_ShouldRefundVisits()
    {
        var targetClass = await _dbContext.Classes
            .FirstAsync(c => c.StartsAt > DateTime.UtcNow.AddHours(2) && !c.IsCancelled);

        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user002@club.test");
        var pass = await _dbContext.Passes.FirstAsync(p => p.UserId == user.Id);
        var initialVisits = pass.RemainingVisits;

        var enrollment = await _dbContext.Enrollments.FirstOrDefaultAsync(e => e.UserId == user.Id && e.ClassId == targetClass.Id && e.CancelledAt == null);

        var result = await _service.CancelEnrollmentAsync(user.Id, enrollment.Id, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(enrollment.Id, result.Id);
        Assert.AreEqual("cancelled", result.Status);
        Assert.AreEqual(enrollment.Seats, result.RefundedVisits);

        await _dbContext.Entry(enrollment).ReloadAsync();
        Assert.AreEqual("cancelled", enrollment.Status);
        Assert.IsNotNull(enrollment.CancelledAt);

        await _dbContext.Entry(pass).ReloadAsync();
        Assert.AreEqual(initialVisits + enrollment.Seats, pass.RemainingVisits);
    }

    [TestMethod]
    public async Task CancelEnrollment_LessThan2Hours_ShouldNotRefundVisits()
    {
    var now = DateTime.UtcNow;
    
    var soonClasses = await _dbContext.Classes
        .Where(c => c.StartsAt > now 
                 && c.StartsAt <= now.AddHours(2) 
                 && !c.IsCancelled)
        .ToListAsync();
    
    var soonClassIds = soonClasses.Select(c => c.Id).ToList();
    
    var enrollment = await _dbContext.Enrollments
        .Include(e => e.Class)
        .Where(e => soonClassIds.Contains(e.ClassId)
                 && e.Status == "active"
                 && e.CancelledAt == null)
        .FirstOrDefaultAsync();

    
    var user = await _dbContext.Users.FirstAsync(u => u.Id == enrollment.UserId);
    var pass = await _dbContext.Passes.FirstAsync(p => p.UserId == user.Id);
    var initialVisits = pass.RemainingVisits;
    
    var result = await _service.CancelEnrollmentAsync(user.Id, enrollment.Id, CancellationToken.None);
    
    Assert.IsNotNull(result);
    Assert.AreEqual(enrollment.Id, result.Id);
    Assert.AreEqual("cancelled", result.Status);
    Assert.AreEqual(0, result.RefundedVisits);
    
    await _dbContext.Entry(pass).ReloadAsync();
    Assert.AreEqual(initialVisits, pass.RemainingVisits);
    
    await _dbContext.Entry(enrollment).ReloadAsync();
    Assert.AreEqual("cancelled", enrollment.Status);
    Assert.IsNotNull(enrollment.CancelledAt);
    }

    [TestMethod]
    public async Task CancelEnrollment_AlreadyCancelled_ShouldReturnRefundedVisits()
    {
        var enrollment = await _dbContext.Enrollments.FirstAsync(e => e.CancelledAt != null);
        var user = await _dbContext.Users.FirstAsync(u => u.Id == enrollment.UserId);
        var pass = await _dbContext.Passes.FirstAsync(p => p.UserId == user.Id);
        var initialVisits = pass.RemainingVisits;

        var result = await _service.CancelEnrollmentAsync(user.Id, enrollment.Id, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual("cancelled", result.Status);
        Assert.AreEqual(enrollment.Seats, result.RefundedVisits);

        await _dbContext.Entry(pass).ReloadAsync();
        Assert.AreEqual(initialVisits, pass.RemainingVisits);
    }

    [TestMethod]
    public async Task CancelEnrollment_ClassCancelled_ShouldRefundVisits()
    {
        var cancelledClass = await _dbContext.Classes.FirstAsync(c => c.IsCancelled && c.StartsAt > DateTime.UtcNow);

        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user004@club.test");
        var pass = await _dbContext.Passes.FirstAsync(p => p.UserId == user.Id);
        var initialVisits = pass.RemainingVisits;

        var enrollment = new Enrollment
        {
            UserId = user.Id,
            ClassId = cancelledClass.Id,
            Seats = 1,
            Status = "active",
            CreatedAt = DateTime.UtcNow
        };
        _dbContext.Enrollments.Add(enrollment);
        pass.RemainingVisits -= 1;
        await _dbContext.SaveChangesAsync();
        initialVisits = pass.RemainingVisits;

        var result = await _service.CancelEnrollmentAsync(user.Id, enrollment.Id, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual("cancelled", result.Status);
        Assert.AreEqual(enrollment.Seats, result.RefundedVisits);

        await _dbContext.Entry(enrollment).ReloadAsync();
        Assert.AreEqual("cancelled", enrollment.Status);

        await _dbContext.Entry(pass).ReloadAsync();
        Assert.AreEqual(initialVisits + enrollment.Seats, pass.RemainingVisits);
    }


    [TestMethod]
    public async Task CancelEnrollment_EnrollmentNotFound_ThrowsNotFoundException()
    {
        var user = await _dbContext.Users.FirstAsync();

        await Assert.ThrowsExceptionAsync<NotFoundException>(() =>
            _service.CancelEnrollmentAsync(user.Id, 999999, CancellationToken.None));
    }

    [TestMethod]
    public async Task CancelEnrollment_WrongUserId_ThrowsNotFoundException()
    {
        var enrollment = await _dbContext.Enrollments.FirstAsync(e => e.CancelledAt == null);
        var anotherUser = await _dbContext.Users.FirstAsync(u => u.Id != enrollment.UserId);

        await Assert.ThrowsExceptionAsync<NotFoundException>(() =>
            _service.CancelEnrollmentAsync(anotherUser.Id, enrollment.Id, CancellationToken.None));
    }

    [TestMethod]
    public async Task CancelEnrollment_ClassStarted_ThrowsConflictException()
    {
        var enrollment = await _dbContext.Enrollments
            .Include(e => e.Class)
            .FirstAsync(e => e.CancelledAt == null && e.Class.StartsAt <= DateTime.UtcNow);

        var ex = await Assert.ThrowsExceptionAsync<ConflictException>(() =>
            _service.CancelEnrollmentAsync(enrollment.UserId, enrollment.Id, CancellationToken.None));

        Assert.AreEqual(ExceptionNames.ClassStarted, ex.Reason);
    }

    [TestMethod]
    public async Task GetEnrollment_ValidEnrollment_ShouldReturnEnrollmentDetails()
    {
        var enrollment = await _dbContext.Enrollments
            .Include(e => e.Class)
            .ThenInclude(c => c.Room)
            .FirstAsync(e => e.CancelledAt == null);

        var user = await _dbContext.Users.FirstAsync(u => u.Id == enrollment.UserId);
        var pass = await _dbContext.Passes.FirstAsync(p => p.UserId == user.Id);

        var result = await _service.GetEnrollmentAsync(user.Id, enrollment.Id, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(enrollment.Id, result.Id);
        Assert.AreEqual(enrollment.Status, result.Status);
        Assert.AreEqual(enrollment.Seats, result.Seats);
        Assert.AreEqual(enrollment.CreatedAt, result.CreatedAt);

        Assert.IsNotNull(result.Class);
        Assert.AreEqual(enrollment.ClassId, result.Class.Id);
        Assert.AreEqual(enrollment.Class.Title, result.Class.Title);
        Assert.AreEqual(enrollment.Class.StartsAt, result.Class.StartsAt);
        Assert.AreEqual(enrollment.Class.DurationMinutes, result.Class.DurationMinutes);
        Assert.AreEqual(enrollment.Class.IsCancelled, result.Class.IsCanceled);

        Assert.IsNotNull(result.Class.Room);
        Assert.AreEqual(enrollment.Class.RoomId, result.Class.Room.Id);
        Assert.AreEqual(enrollment.Class.Room.Name, result.Class.Room.Name);

        Assert.AreEqual(pass.RemainingVisits, result.Pass.RemainingVisits);
        Assert.AreEqual(pass.ValidUntil, result.Pass.ValidUntil);
    }

    [TestMethod]
    public async Task GetEnrollment_CachedResponse_ShouldReturnFromCache()
    {
        var enrollment = await _dbContext.Enrollments
            .Include(e => e.Class)
            .ThenInclude(c => c.Room)
            .FirstAsync(e => e.CancelledAt == null);

        var user = await _dbContext.Users.FirstAsync(u => u.Id == enrollment.UserId);
        var pass = await _dbContext.Passes.FirstAsync(p => p.UserId == user.Id);

        var result1 = await _service.GetEnrollmentAsync(user.Id, enrollment.Id, CancellationToken.None);

        await FlushCacheAsync();

        var result2 = await _service.GetEnrollmentAsync(user.Id, enrollment.Id, CancellationToken.None);

        Assert.IsNotNull(result2);
        Assert.AreEqual(result1.Id, result2.Id);
        Assert.AreEqual(result1.Status, result2.Status);
        Assert.AreEqual(result1.Seats, result2.Seats);

        var cacheKey = $"userId={user.Id}:enrollmentId={enrollment.Id}";
        var cached = await _cacheService.GetAsync<GetEnrollmentResponse>(cacheKey, CancellationToken.None);
        Assert.IsNotNull(cached);
        Assert.AreEqual(result2.Id, cached.Id);
    }

    [TestMethod]
    public async Task GetEnrollment_ShouldCacheResponseAfterFirstCall()
    {
        var enrollment = await _dbContext.Enrollments
            .Include(e => e.Class)
            .ThenInclude(c => c.Room)
            .FirstAsync(e => e.CancelledAt == null);

        var user = await _dbContext.Users.FirstAsync(u => u.Id == enrollment.UserId);

        await _service.GetEnrollmentAsync(user.Id, enrollment.Id, CancellationToken.None);

        var cacheKey = $"userId={user.Id}:enrollmentId={enrollment.Id}";
        var cached = await _cacheService.GetAsync<GetEnrollmentResponse>(cacheKey, CancellationToken.None);

        Assert.IsNotNull(cached);
        Assert.AreEqual(enrollment.Id, cached.Id);
        Assert.AreEqual(enrollment.Status, cached.Status);
        Assert.AreEqual(enrollment.Seats, cached.Seats);
    }

    [TestMethod]
    public async Task GetEnrollment_EnrollmentNotFound_ShouldThrowNotFoundException()
    {
        var user = await _dbContext.Users.FirstAsync();
        var nonExistentEnrollmentId = 999999L;

        var exception = await Assert.ThrowsExceptionAsync<NotFoundException>(() =>
            _service.GetEnrollmentAsync(user.Id, nonExistentEnrollmentId, CancellationToken.None));

        Assert.AreEqual(ExceptionNames.EnrollmentIsNotExists, exception.Detail);
    }

    [TestMethod]
    public async Task GetEnrollment_WrongUserId_ShouldThrowNotFoundException()
    {
        var enrollment = await _dbContext.Enrollments.FirstAsync(e => e.CancelledAt == null);

        var anotherUser = await _dbContext.Users.FirstAsync(u => u.Id != enrollment.UserId);

        var exception = await Assert.ThrowsExceptionAsync<NotFoundException>(() =>
            _service.GetEnrollmentAsync(anotherUser.Id, enrollment.Id, CancellationToken.None));

        Assert.AreEqual(ExceptionNames.EnrollmentIsNotExists, exception.Detail);
    }

    [TestMethod]
    public async Task GetEnrollment_CancelledEnrollment_ShouldReturnCancelledStatus()
    {
        var enrollment = await _dbContext.Enrollments
            .Include(e => e.Class)
            .ThenInclude(c => c.Room)
            .FirstAsync(e => e.CancelledAt != null);

        var user = await _dbContext.Users.FirstAsync(u => u.Id == enrollment.UserId);
        var pass = await _dbContext.Passes.FirstAsync(p => p.UserId == user.Id);

        var result = await _service.GetEnrollmentAsync(user.Id, enrollment.Id, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(enrollment.Id, result.Id);
        Assert.AreEqual("cancelled", result.Status);
        Assert.AreEqual(enrollment.Seats, result.Seats);

        Assert.IsNotNull(result.Class);
        Assert.IsNotNull(result.Class.Room);

        Assert.AreEqual(pass.RemainingVisits, result.Pass.RemainingVisits);
        Assert.AreEqual(pass.ValidUntil, result.Pass.ValidUntil);
    }

    [TestMethod]
    public async Task JoinWaitlist_ValidRequest_ShouldCreateWaitlistAndReturnPosition()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user002@club.test");

        var targetClass = await _dbContext.Classes.FirstAsync(c => c.StartsAt > DateTime.UtcNow && !c.IsCancelled);

        var request = new JoinWaitlistRequest { ClassId = targetClass.Id, Seats = 999 };
        var idempotencyKey = Guid.NewGuid().ToString();

        var result = await _service.JoinWaitlistAsync(request, user.Id, idempotencyKey, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(targetClass.Id, result.ClassId);
        Assert.AreEqual(999, result.Seats);
        Assert.IsTrue(result.Position >= 1, "Позиция должна быть >= 1");

        var dbWaitlist = await _dbContext.Waitlists.FirstOrDefaultAsync(w => w.UserId == user.Id && w.ClassId == targetClass.Id);
        Assert.IsNotNull(dbWaitlist);
        Assert.AreEqual("active", dbWaitlist.Status);

        var cacheKey = $"{user.Id}:{idempotencyKey}";
        var cached = await _cacheService.GetAsync<JoinWaitlistResponse>(cacheKey, CancellationToken.None);
        Assert.IsNotNull(cached);
        Assert.AreEqual(result.Id, cached.Id);
    }

    [TestMethod]
    public async Task JoinWaitlist_Idempotency_ShouldReturnCachedResponse()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user003@club.test");
        var targetClass = await _dbContext.Classes.FirstAsync(c => c.StartsAt > DateTime.UtcNow && !c.IsCancelled);
        var request = new JoinWaitlistRequest { ClassId = targetClass.Id, Seats = 999 };
        var idempotencyKey = "test-idempotency-key-unique";

        var result1 = await _service.JoinWaitlistAsync(request, user.Id, idempotencyKey, CancellationToken.None);
        var result2 = await _service.JoinWaitlistAsync(request, user.Id, idempotencyKey, CancellationToken.None);

        Assert.AreEqual(result1.Id, result2.Id);

        var count = await _dbContext.Waitlists.CountAsync(w => w.UserId == user.Id && w.ClassId == targetClass.Id);
        Assert.AreEqual(1, count);
    }

    [TestMethod]
    public async Task JoinWaitlist_ClassNotFound_ThrowsNotFoundException()
    {
        var user = await _dbContext.Users.FirstAsync();
        var request = new JoinWaitlistRequest { ClassId = 999999, Seats = 1 };

        var ex = await Assert.ThrowsExceptionAsync<NotFoundException>(() =>
            _service.JoinWaitlistAsync(request, user.Id, Guid.NewGuid().ToString(), CancellationToken.None));

        Assert.AreEqual(ExceptionNames.ClassIsNotExists, ex.Detail);
    }

    [TestMethod]
    public async Task JoinWaitlist_ClassCancelled_ThrowsConflictException()
    {
        var targetClass = await _dbContext.Classes.FirstAsync(c => c.IsCancelled && c.StartsAt > DateTime.UtcNow);

        var user = await _dbContext.Users.FirstAsync();
        var request = new JoinWaitlistRequest { ClassId = targetClass.Id, Seats = 1 };

        var ex = await Assert.ThrowsExceptionAsync<ConflictException>(() =>
            _service.JoinWaitlistAsync(request, user.Id, Guid.NewGuid().ToString(), CancellationToken.None));

        Assert.AreEqual(ExceptionNames.ClassCancelled, ex.Reason);
    }

    [TestMethod]
    public async Task JoinWaitlist_ClassStarted_ThrowsConflictException()
    {
        var targetClass = await _dbContext.Classes.FirstAsync(c => c.StartsAt <= DateTime.UtcNow && !c.IsCancelled);

        var user = await _dbContext.Users.FirstAsync();
        var request = new JoinWaitlistRequest { ClassId = targetClass.Id, Seats = 1 };

        var ex = await Assert.ThrowsExceptionAsync<ConflictException>(() =>
            _service.JoinWaitlistAsync(request, user.Id, Guid.NewGuid().ToString(), CancellationToken.None));

        Assert.AreEqual(ExceptionNames.ClassStarted, ex.Reason);
    }

    [TestMethod]
    public async Task JoinWaitlist_AlreadyWaiting_ThrowsConflictException()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user005@club.test");

        var existingWaitlist = await _dbContext.Waitlists
            .Include(w => w.Class)
            .FirstAsync(w => w.UserId == user.Id && w.Status == "active");

        var request = new JoinWaitlistRequest { ClassId = existingWaitlist.ClassId, Seats = 1 };

        var ex = await Assert.ThrowsExceptionAsync<ConflictException>(() =>
            _service.JoinWaitlistAsync(request, user.Id, Guid.NewGuid().ToString(), CancellationToken.None));

        Assert.AreEqual(ExceptionNames.AlreadyWaiting, ex.Reason);
    }

    [TestMethod]
    public async Task JoinWaitlist_SeatsAvailable_ThrowsConflictException()
    {
        var targetClass = await _dbContext.Classes
            .Include(c => c.Enrollments)
            .Where(c => !c.IsCancelled && c.StartsAt > DateTime.UtcNow)
            .Select(c => new
            {
                Class = c,
                FreeSeats = c.Capacity - c.Enrollments.Where(e => e.Status == "active").Sum(e => e.Seats)
            })
            .Where(x => x.FreeSeats >= 2)
            .FirstAsync();

        var user = await _dbContext.Users.FirstAsync(u => u.Email != "user005@club.test");
        var request = new JoinWaitlistRequest { ClassId = targetClass.Class.Id, Seats = 1 };

        var ex = await Assert.ThrowsExceptionAsync<ConflictException>(() =>
            _service.JoinWaitlistAsync(request, user.Id, Guid.NewGuid().ToString(), CancellationToken.None));

        Assert.AreEqual(ExceptionNames.SeatsAvailable, ex.Reason);
    }

    [TestMethod]
    public async Task CancelWaitlist_ActiveWaitlist_ShouldCancelAndReturnResponse()
    {
        var waitlist = await _dbContext.Waitlists.FirstAsync(w => w.Status == "active");

        var user = await _dbContext.Users.FirstAsync(u => u.Id == waitlist.UserId);

        var result = await _service.CancelWaitlistAsync(user.Id, waitlist.Id, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(waitlist.Id, result.Id);
        Assert.AreEqual("cancelled", result.Status);

        await _dbContext.Entry(waitlist).ReloadAsync();
        Assert.AreEqual("cancelled", waitlist.Status);
    }

    [TestMethod]
    public async Task CancelWaitlist_AlreadyCancelled_ShouldReturnSuccessIdempotently()
    {
        var waitlist = await _dbContext.Waitlists.FirstAsync(w => w.Status == "active");

        var user = await _dbContext.Users.FirstAsync(u => u.Id == waitlist.UserId);

        var result1 = await _service.CancelWaitlistAsync(user.Id, waitlist.Id, CancellationToken.None);

        var result2 = await _service.CancelWaitlistAsync(user.Id, waitlist.Id, CancellationToken.None);

        Assert.IsNotNull(result2);
        Assert.AreEqual(result1.Id, result2.Id);
        Assert.AreEqual("cancelled", result2.Status);

        await _dbContext.Entry(waitlist).ReloadAsync();
        Assert.AreEqual("cancelled", waitlist.Status);
    }

    [TestMethod]
    public async Task CancelWaitlist_WaitlistNotFound_ThrowsNotFoundException()
    {
        var user = await _dbContext.Users.FirstAsync();
        var nonExistentWaitlistId = 999;

        var exception = await Assert.ThrowsExceptionAsync<NotFoundException>(() =>
            _service.CancelWaitlistAsync(user.Id, nonExistentWaitlistId, CancellationToken.None));

        Assert.AreEqual(ExceptionNames.WaitlistIsNotExists, exception.Detail);
    }

    [TestMethod]
    public async Task CancelWaitlist_WrongUserId_ThrowsNotFoundException()
    {
        var waitlist = await _dbContext.Waitlists.FirstAsync(w => w.Status == "active");

        var anotherUser = await _dbContext.Users.FirstAsync(u => u.Id != waitlist.UserId);

        var exception = await Assert.ThrowsExceptionAsync<NotFoundException>(() =>
            _service.CancelWaitlistAsync(anotherUser.Id, waitlist.Id, CancellationToken.None));

        Assert.AreEqual(ExceptionNames.WaitlistIsNotExists, exception.Detail);
    }

    private async Task FlushCacheAsync()
    {
        var options = ConfigurationOptions.Parse(TestDatabase.RedisConnectionString);
        options.AllowAdmin = true;

        using var connection = await ConnectionMultiplexer.ConnectAsync(options);
        foreach (var endpoint in connection.GetEndPoints())
        {
            await connection.GetServer(endpoint).FlushDatabaseAsync(options.DefaultDatabase ?? 0);
        }
    }
}
