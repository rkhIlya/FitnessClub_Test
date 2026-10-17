using DataAccessLayer;
using DataAccessLayer.UnitOfWork;
using Domain.Request;
using Domain.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace WebApi.Tests.Services;

[TestClass]
public class AnalyticsServiceTests
{
    private IServiceProvider _serviceProvider;
    private IServiceScope _scope;
    private ClubDbContext _dbContext;
    private AnalyticsService _service;

    [TestInitialize]
    public async Task TestInitialize()
    {
        var services = new ServiceCollection();

        services.AddDbContext<ClubDbContext>(o =>
            o.UseNpgsql(TestDatabase.PostgresConnectionString));

        services.AddScoped<IUnitOfWork, UnitOfWork>();

        _serviceProvider = services.BuildServiceProvider();

        var scope = _serviceProvider.CreateScope();
        _dbContext = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        _service = new AnalyticsService(unitOfWork);
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
    public async Task LoadAnalytics_AllRoomsWideRange_ShouldReturnAggregatedData()
    {
        var now = DateTime.UtcNow;
        var request = new LoadAnalyticsRequest
        {
            From = DateOnly.FromDateTime(now.AddDays(-40)),
            To = DateOnly.FromDateTime(now.AddDays(40)),
            RoomId = null
        };

        var result = await _service.LoadAnalyticsAsync(request, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.IsNotNull(result.Items);
        Assert.IsTrue(result.Items.Count > 0, "Ожидались данные аналитики, но список пуст");

        var uniqueRooms = result.Items.Select(x => x.Room.Id).Distinct().Count();
        Assert.IsTrue(uniqueRooms > 1, "Ожидались данные по нескольким комнатам");

        var firstItem = result.Items.First();
        Assert.IsNotNull(firstItem.Room);
        Assert.IsTrue(firstItem.Room.Id > 0);
        Assert.IsFalse(string.IsNullOrEmpty(firstItem.Room.Name));
        Assert.IsTrue(firstItem.ClassesCount >= 0);
        Assert.IsTrue(firstItem.BookedSeats >= 0);
        Assert.IsTrue(firstItem.TotalCapacity >= 0);
    }

    [TestMethod]
    public async Task LoadAnalytics_SpecificRoom_ShouldReturnDataOnlyForThatRoom()
    {
        var targetRoom = await _dbContext.Rooms.FirstAsync();

        var now = DateTime.UtcNow;
        var request = new LoadAnalyticsRequest
        {
            From = DateOnly.FromDateTime(now.AddDays(-40)),
            To = DateOnly.FromDateTime(now.AddDays(40)),
            RoomId = targetRoom.Id
        };

        var result = await _service.LoadAnalyticsAsync(request, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.IsNotNull(result.Items);

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
    public async Task LoadAnalytics_EmptyDateRange_ShouldReturnEmptyList()
    {
        var request = new LoadAnalyticsRequest
        {
            From = DateOnly.FromDateTime(new DateTime(2000, 1, 1)),
            To = DateOnly.FromDateTime(new DateTime(2000, 12, 31)),
            RoomId = null
        };

        var result = await _service.LoadAnalyticsAsync(request, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.IsNotNull(result.Items);
        Assert.AreEqual(0, result.Items.Count);
    }
}
