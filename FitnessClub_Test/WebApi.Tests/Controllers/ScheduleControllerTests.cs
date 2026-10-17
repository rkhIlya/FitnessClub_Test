using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Common.Caching;
using DataAccessLayer;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using Domain.Response;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using WebApi.Auth;

namespace WebApi.Tests.Controllers;

[TestClass]
public class ScheduleControllerTests
{
    private WebApplicationFactory<Program> _factory;
    private HttpClient _client;
    private IServiceScope _scope;
    private ClubDbContext _dbContext;
    private AccessTokenService _tokenService;

    [TestInitialize]
    public async Task TestInitialize()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.Configure<JwtOptions>(options =>
                {
                    options.Key = "5f2a9c41e7b84d0ba63c8e19d472af5c0916b3ed7c8241a5fe36b90d4c17e28a";
                    options.Issuer = "club-api";
                    options.Audience = "club-api";
                    options.LifetimeMinutes = 720;
                });

                services.AddDbContext<ClubDbContext>(o =>
                    o.UseNpgsql(TestDatabase.PostgresConnectionString));

                services.Configure<RedisCacheSettings>(settings =>
                {
                    settings.DefaultExpiration = TimeSpan.FromHours(1);
                });

                services.AddSingleton<IConnectionMultiplexer>(_ =>
                    ConnectionMultiplexer.Connect(TestDatabase.RedisConnectionString));

                services.AddScoped<IUnitOfWork, UnitOfWork>();
            });
        });

        _client = _factory.CreateClient();

        var scopeFactory = _factory.Services.GetRequiredService<IServiceScopeFactory>();
        _scope = scopeFactory.CreateScope();
        _dbContext = _scope.ServiceProvider.GetRequiredService<ClubDbContext>();

        var jwtOptions = _factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value;
        _tokenService = new AccessTokenService(jwtOptions);
    }

    [TestCleanup]
    public async Task TestCleanup()
    {
        _scope?.Dispose();
        _client?.Dispose();

        if (_factory is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else
        {
            _factory?.Dispose();
        }
    }

    [TestMethod]
    public async Task GetSchedule_BasicRequest_ReturnsOkWithItemsAndSeats()
    {
        var enrollment = await _dbContext.Enrollments
            .Include(e => e.Class)
            .ThenInclude(c => c.Room)
            .FirstAsync(e => e.Status == "active");

        var user = await _dbContext.Users.FirstAsync(u => u.Id == enrollment.UserId);
        var token = GenerateValidToken(user);

        var from = enrollment.Class.StartsAt.AddDays(-1).ToString("O");
        var to = enrollment.Class.StartsAt.AddDays(1).ToString("O");
        var queryString = $"?from={HttpUtility.UrlEncode(from)}&to={HttpUtility.UrlEncode(to)}";

        var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"/schedule{queryString}");
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(httpRequest);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<GetScheduleResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.IsNotNull(result);
        Assert.IsNotNull(result.Items);
        Assert.IsTrue(result.Items.Count > 0);

        var targetItem = result.Items.FirstOrDefault(i => i.ClassId == enrollment.ClassId);
        Assert.IsNotNull(targetItem);
        Assert.AreEqual(enrollment.Seats, targetItem.MySeats);
        Assert.AreEqual(enrollment.Class.Room.Id, targetItem.Room.Id);
    }

    [TestMethod]
    public async Task GetSchedule_FilterByRoomId_ReturnsOnlyMatchingClasses()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user001@club.test");
        var token = GenerateValidToken(user);

        var room = await _dbContext.Rooms.FirstAsync();
        var futureClass = await _dbContext.Classes.FirstAsync(c => c.RoomId == room.Id && c.StartsAt > DateTime.UtcNow);

        var from = futureClass.StartsAt.AddDays(-1).ToString("O");
        var to = futureClass.StartsAt.AddDays(1).ToString("O");
        var queryString = $"?from={HttpUtility.UrlEncode(from)}&to={HttpUtility.UrlEncode(to)}&roomId={room.Id}";

        var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"/schedule{queryString}");
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(httpRequest);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<GetScheduleResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.IsNotNull(result);

        foreach (var item in result.Items)
        {
            Assert.AreEqual(room.Id, item.Room.Id);
        }
    }

    [TestMethod]
    public async Task GetSchedule_WithLimit_ReturnsContinuationToken()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user002@club.test");
        var token = GenerateValidToken(user);

        var futureClass = await _dbContext.Classes.FirstAsync(c => c.StartsAt > DateTime.UtcNow);
        var from = futureClass.StartsAt.AddDays(-10).ToString("O");
        var to = futureClass.StartsAt.AddDays(10).ToString("O");

        var queryString = $"?from={HttpUtility.UrlEncode(from)}&to={HttpUtility.UrlEncode(to)}&limit=2";

        var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"/schedule{queryString}");
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(httpRequest);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<GetScheduleResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.IsNotNull(result);
        Assert.IsNotNull(result.ContinuationToken);
        Assert.IsTrue(result.Items.Count <= 2);
    }

    [TestMethod]
    public async Task GetSchedule_WithValidContinuationToken_ReturnsNextPage()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user003@club.test");
        var token = GenerateValidToken(user);

        var futureClass = await _dbContext.Classes.FirstAsync(c => c.StartsAt > DateTime.UtcNow);
        var from = futureClass.StartsAt.AddDays(-10).ToString("O");
        var to = futureClass.StartsAt.AddDays(10).ToString("O");

        var firstQueryString = $"?from={HttpUtility.UrlEncode(from)}&to={HttpUtility.UrlEncode(to)}&limit=2";
        var firstRequest = new HttpRequestMessage(HttpMethod.Get, $"/schedule{firstQueryString}");
        firstRequest.Headers.Add("Authorization", $"Bearer {token}");

        var firstResponse = await _client.SendAsync(firstRequest);
        var firstResult = await firstResponse.Content.ReadFromJsonAsync<GetScheduleResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.IsNotNull(firstResult.ContinuationToken);

        var secondQueryString = $"?from={HttpUtility.UrlEncode(from)}&to={HttpUtility.UrlEncode(to)}&limit=2&continuationToken={HttpUtility.UrlEncode(firstResult.ContinuationToken)}";
        var secondRequest = new HttpRequestMessage(HttpMethod.Get, $"/schedule{secondQueryString}");
        secondRequest.Headers.Add("Authorization", $"Bearer {token}");

        var secondResponse = await _client.SendAsync(secondRequest);

        Assert.AreEqual(HttpStatusCode.OK, secondResponse.StatusCode);

        var secondResult = await secondResponse.Content.ReadFromJsonAsync<GetScheduleResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.IsNotNull(secondResult);
    }

    [TestMethod]
    public async Task GetSchedule_InvalidContinuationToken_Returns400()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user004@club.test");
        var token = GenerateValidToken(user);

        var futureClass = await _dbContext.Classes.FirstAsync(c => c.StartsAt > DateTime.UtcNow);
        var from = futureClass.StartsAt.AddDays(-1).ToString("O");
        var to = futureClass.StartsAt.AddDays(1).ToString("O");

        var queryString = $"?from={HttpUtility.UrlEncode(from)}&to={HttpUtility.UrlEncode(to)}&continuationToken=invalid_base64_string_!@#";

        var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"/schedule{queryString}");
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(httpRequest);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task GetSchedule_EmptyDateRange_ReturnsEmptyItemsAndNullToken()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user005@club.test");
        var token = GenerateValidToken(user);

        var from = DateTime.UtcNow.AddYears(10).ToString("O");
        var to = DateTime.UtcNow.AddYears(10).AddDays(1).ToString("O");
        var queryString = $"?from={HttpUtility.UrlEncode(from)}&to={HttpUtility.UrlEncode(to)}";

        var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"/schedule{queryString}");
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(httpRequest);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<GetScheduleResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.IsNotNull(result);
        Assert.IsNotNull(result.Items);
        Assert.AreEqual(0, result.Items.Count);
        Assert.IsNull(result.ContinuationToken);
    }

    private string GenerateValidToken(User user)
    {
        var response = _tokenService.Create(user);
        return response.AccessToken;
    }
}
