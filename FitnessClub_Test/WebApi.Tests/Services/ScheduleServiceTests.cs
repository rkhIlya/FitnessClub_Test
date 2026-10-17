using System.Text;
using DataAccessLayer;
using DataAccessLayer.UnitOfWork;
using Domain.Exceptions;
using Domain.Interface;
using Domain.Request;
using Domain.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace WebApi.Tests.Services;

[TestClass]
public class ScheduleServiceTests
{
    private IServiceProvider _serviceProvider;
    private IServiceScope _scope;
    private ClubDbContext _dbContext;
    private ScheduleService _service;

    [TestInitialize]
    public async Task TestInitialize()
    {
        var services = new ServiceCollection();

        services.AddDbContext<ClubDbContext>(o =>
            o.UseNpgsql(TestDatabase.PostgresConnectionString));

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IScheduleService, ScheduleService>();

        _serviceProvider = services.BuildServiceProvider();
        _scope = _serviceProvider.CreateScope();

        _dbContext = _scope.ServiceProvider.GetRequiredService<ClubDbContext>();

        var unitOfWork = _scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        _service = new ScheduleService(unitOfWork);
    }

    [TestCleanup]
    public async Task TestCleanup()
    {
        _scope?.Dispose();
        if (_serviceProvider is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task GetSchedule_ValidRange_ShouldReturnClassesWithPagination()
    {
        var now = DateTime.UtcNow;
        var request = new GetScheduleRequest
        {
            From = now.AddDays(-10),
            To = now.AddDays(10),
            RoomId = null,
            Limit = 5,
            ContinuationToken = null
        };

        var result = await _service.GetScheduleAsync(request, userId: 1, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.IsNotNull(result.Items);
        Assert.IsTrue(result.Items.Count <= 5, "Количество элементов не должно превышать лимит");

        foreach (var item in result.Items)
        {
            Assert.IsTrue(item.ClassId > 0);
            Assert.IsFalse(string.IsNullOrEmpty(item.Title));
            Assert.IsNotNull(item.Room);
            Assert.IsTrue(item.Room.Id > 0);
            Assert.IsFalse(string.IsNullOrEmpty(item.Room.Name));
            Assert.IsTrue(item.Capacity > 0);
            Assert.IsTrue(item.FreeSeats >= 0);
            Assert.IsTrue(item.FreeSeats <= item.Capacity);
            Assert.IsTrue(item.MySeats >= 0);
        }

        if (result.Items.Count == 5)
        {
            Assert.IsNotNull(result.ContinuationToken, "При наличии следующих страниц должен быть токен");
        }
    }

    [TestMethod]
    public async Task GetSchedule_WithContinuationToken_ShouldReturnNextPage()
    {
        var now = DateTime.UtcNow;
        var request1 = new GetScheduleRequest
        {
            From = now.AddDays(-40),
            To = now.AddDays(40),
            RoomId = null,
            Limit = 3,
            ContinuationToken = null
        };

        var result1 = await _service.GetScheduleAsync(request1, userId: 1, CancellationToken.None);

        Assert.IsNotNull(result1.ContinuationToken, "Ожидался токен для следующей страницы");

        var request2 = new GetScheduleRequest
        {
            From = now.AddDays(-40),
            To = now.AddDays(40),
            RoomId = null,
            Limit = 3,
            ContinuationToken = result1.ContinuationToken
        };

        var result2 = await _service.GetScheduleAsync(request2, userId: 1, CancellationToken.None);

        Assert.IsNotNull(result2);
        Assert.IsNotNull(result2.Items);

        var ids1 = result1.Items.Select(i => i.ClassId).ToHashSet();
        var ids2 = result2.Items.Select(i => i.ClassId).ToHashSet();

        Assert.IsFalse(ids1.Overlaps(ids2), "Страницы не должны содержать одинаковые классы");
    }

    [TestMethod]
    public async Task GetSchedule_EmptyRange_ShouldReturnEmptyList()
    {
        var request = new GetScheduleRequest
        {
            From = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2000, 12, 31, 23, 59, 59, DateTimeKind.Utc),
            RoomId = null,
            Limit = 10,
            ContinuationToken = null
        };

        var result = await _service.GetScheduleAsync(request, userId: 1, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.IsNotNull(result.Items);
        Assert.AreEqual(0, result.Items.Count);
        Assert.IsNull(result.ContinuationToken);
    }

    [TestMethod]
    public async Task GetSchedule_FilterByRoom_ShouldReturnOnlyThatRoom()
    {
        var targetRoom = await _dbContext.Rooms.FirstAsync();
        var now = DateTime.UtcNow;

        var request = new GetScheduleRequest
        {
            From = now.AddDays(-40),
            To = now.AddDays(40),
            RoomId = targetRoom.Id,
            Limit = 100,
            ContinuationToken = null
        };

        var result = await _service.GetScheduleAsync(request, userId: 1, CancellationToken.None);

        Assert.IsNotNull(result);

        if (result.Items.Count > 0)
        {
            foreach (var item in result.Items)
            {
                Assert.AreEqual(targetRoom.Id, item.Room.Id);
                Assert.AreEqual(targetRoom.Name, item.Room.Name);
            }
        }
    }

    [TestMethod]
    public async Task GetSchedule_VerifyFreeSeatsCalculation()
    {
        var targetClass = await _dbContext.Classes
            .Include(c => c.Enrollments)
            .FirstAsync(c => c.StartsAt > DateTime.UtcNow && !c.IsCancelled);

        var totalBooked = await _dbContext.Enrollments
            .Where(e => e.ClassId == targetClass.Id && e.Status == "active" && e.CancelledAt == null)
            .SumAsync(e => e.Seats);

        var expectedFreeSeats = Math.Max(0, targetClass.Capacity - totalBooked);

        var now = DateTime.UtcNow;
        var request = new GetScheduleRequest
        {
            From = targetClass.StartsAt.AddMinutes(-1),
            To = targetClass.StartsAt.AddMinutes(1),
            RoomId = null,
            Limit = 100,
            ContinuationToken = null
        };

        var result = await _service.GetScheduleAsync(request, userId: 1, CancellationToken.None);

        var item = result.Items.FirstOrDefault(i => i.ClassId == targetClass.Id);
        Assert.IsNotNull(item, "Класс должен быть в результате");
        Assert.AreEqual(expectedFreeSeats, item.FreeSeats);
    }

    [TestMethod]
    public async Task GetSchedule_VerifyMySeatsCalculation()
    {
        var userWithEnrollments = await _dbContext.Enrollments
            .Where(e => e.Status == "active" && e.CancelledAt == null)
            .Select(e => e.UserId)
            .FirstAsync();

        var targetClass = await _dbContext.Classes
            .Include(c => c.Enrollments)
            .FirstAsync(c => c.Enrollments.Any(e => e.UserId == userWithEnrollments && e.Status == "active" && e.CancelledAt == null));

        var expectedMySeats = await _dbContext.Enrollments
            .Where(e => e.ClassId == targetClass.Id && e.UserId == userWithEnrollments && e.Status == "active" && e.CancelledAt == null)
            .SumAsync(e => e.Seats);

        var now = DateTime.UtcNow;
        var request = new GetScheduleRequest
        {
            From = targetClass.StartsAt.AddMinutes(-1),
            To = targetClass.StartsAt.AddMinutes(1),
            RoomId = null,
            Limit = 100,
            ContinuationToken = null
        };

        var result = await _service.GetScheduleAsync(request, userId: userWithEnrollments, CancellationToken.None);

        var item = result.Items.FirstOrDefault(i => i.ClassId == targetClass.Id);
        Assert.IsNotNull(item, "Класс должен быть в результате");
        Assert.AreEqual(expectedMySeats, item.MySeats);
    }

    [TestMethod]
    public async Task GetSchedule_InvalidContinuationToken_ShouldThrowException()
    {
        var now = DateTime.UtcNow;
        var request = new GetScheduleRequest
        {
            From = now.AddDays(-10),
            To = now.AddDays(10),
            RoomId = null,
            Limit = 10,
            ContinuationToken = "invalidToken"
        };

        await Assert.ThrowsExceptionAsync<InvalidContinuationTokenException>(() =>
            _service.GetScheduleAsync(request, userId: 1, CancellationToken.None));
    }

    [TestMethod]
    public async Task GetSchedule_MalformedJsonToken_ShouldThrowException()
    {
        var malformedJson = "this is not json";
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(malformedJson));

        var now = DateTime.UtcNow;
        var request = new GetScheduleRequest
        {
            From = now.AddDays(-10),
            To = now.AddDays(10),
            RoomId = null,
            Limit = 10,
            ContinuationToken = token
        };

        await Assert.ThrowsExceptionAsync<InvalidContinuationTokenException>(() =>
            _service.GetScheduleAsync(request, userId: 1, CancellationToken.None));
    }
}
