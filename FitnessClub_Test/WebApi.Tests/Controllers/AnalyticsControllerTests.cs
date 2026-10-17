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
public class AnalyticsControllerTests
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
    public async Task LoadAnalytics_AdminUser_ReturnsOkWithData()
    {
        var admin = await _dbContext.Users.FirstAsync(u => u.Role == "admin");
        var token = GenerateValidToken(admin);

        var earliestClass = await _dbContext.Classes.OrderBy(c => c.StartsAt).FirstAsync();
        var latestClass = await _dbContext.Classes.OrderByDescending(c => c.StartsAt).FirstAsync();

        var from = DateOnly.FromDateTime(earliestClass.StartsAt.AddDays(-1)).ToString("O");
        var to = DateOnly.FromDateTime(latestClass.StartsAt.AddDays(1)).ToString("O");
        var queryString = $"?from={HttpUtility.UrlEncode(from)}&to={HttpUtility.UrlEncode(to)}";

        var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"/analytics/load{queryString}");
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(httpRequest);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<LoadAnalyticsResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.IsNotNull(result);
        Assert.IsNotNull(result.Items);
        Assert.IsTrue(result.Items.Count > 0);

        var firstItem = result.Items[0];
        Assert.IsNotNull(firstItem.Room);
        Assert.IsTrue(firstItem.ClassesCount > 0);
    }

    [TestMethod]
    public async Task LoadAnalytics_RegularUser_Returns403Forbidden()
    {
        var regularUser = await _dbContext.Users.FirstAsync(u => u.Role == "user");
        var token = GenerateValidToken(regularUser);

        var from = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)).ToString("O");
        var to = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)).ToString("O");
        var queryString = $"?from={HttpUtility.UrlEncode(from)}&to={HttpUtility.UrlEncode(to)}";

        var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"/analytics/load{queryString}");
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(httpRequest);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task LoadAnalytics_FilterByRoomId_ReturnsOnlyMatchingRoom()
    {
        var admin = await _dbContext.Users.FirstAsync(u => u.Role == "admin");
        var token = GenerateValidToken(admin);

        var targetRoom = await _dbContext.Rooms.FirstAsync();

        var earliestClass = await _dbContext.Classes.OrderBy(c => c.StartsAt).FirstAsync();
        var latestClass = await _dbContext.Classes.OrderByDescending(c => c.StartsAt).FirstAsync();

        var from = DateOnly.FromDateTime(earliestClass.StartsAt.AddDays(-1)).ToString("O");
        var to = DateOnly.FromDateTime(latestClass.StartsAt.AddDays(1)).ToString("O");
        var queryString = $"?from={HttpUtility.UrlEncode(from)}&to={HttpUtility.UrlEncode(to)}&roomId={targetRoom.Id}";

        var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"/analytics/load{queryString}");
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(httpRequest);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<LoadAnalyticsResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.IsNotNull(result);

        foreach (var item in result.Items)
        {
            Assert.AreEqual(targetRoom.Id, item.Room.Id);
            Assert.AreEqual(targetRoom.Name, item.Room.Name);
        }
    }

    [TestMethod]
    public async Task LoadAnalytics_EmptyDateRange_ReturnsEmptyItems()
    {
        var admin = await _dbContext.Users.FirstAsync(u => u.Role == "admin");
        var token = GenerateValidToken(admin);

        var from = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(10)).ToString("O");
        var to = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(10).AddDays(1)).ToString("O");
        var queryString = $"?from={HttpUtility.UrlEncode(from)}&to={HttpUtility.UrlEncode(to)}";

        var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"/analytics/load{queryString}");
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(httpRequest);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<LoadAnalyticsResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.IsNotNull(result);
        Assert.IsNotNull(result.Items);
        Assert.AreEqual(0, result.Items.Count);
    }

    private string GenerateValidToken(User user)
    {
        var response = _tokenService.Create(user);
        return response.AccessToken;
    }
}
