using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Common.Caching;
using DataAccessLayer;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using Domain.Request;
using Domain.Response;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using WebApi.Auth;

namespace WebApi.Tests.Controllers;

[TestClass]
public class EnrollmentsControllerTests
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
    public async Task CreateEnrollment_ValidRequest_ShouldReturn201Created()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user002@club.test");
        var targetClass = await _dbContext.Classes
            .Where(c => c.StartsAt > DateTime.UtcNow && !c.IsCancelled)
            .OrderByDescending(c => c.StartsAt)
            .FirstAsync();

        var token = GenerateValidToken(user);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/enrollments")
        {
            Content = JsonContent.Create(new CreateEnrollmentRequest { ClassId = targetClass.Id, Seats = 1 })
        };
        httpRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(httpRequest);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<CreateEnrollmentResponse>();
        Assert.IsNotNull(result);
        Assert.AreEqual(targetClass.Id, result.ClassId);
        Assert.AreEqual(1, result.Seats);
        Assert.AreEqual("active", result.Status);
    }

    [TestMethod]
    public async Task CreateEnrollment_MissingIdempotencyKey_ShouldReturn400()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user002@club.test");
        var targetClass = await _dbContext.Classes.FirstAsync(c => c.StartsAt > DateTime.UtcNow);
        var token = GenerateValidToken(user);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/enrollments")
        {
            Content = JsonContent.Create(new CreateEnrollmentRequest { ClassId = targetClass.Id, Seats = 1 })
        };
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(httpRequest);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task CreateEnrollment_InvalidSeats_ShouldReturn400()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user002@club.test");
        var targetClass = await _dbContext.Classes.FirstAsync(c => c.StartsAt > DateTime.UtcNow);
        var token = GenerateValidToken(user);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/enrollments")
        {
            Content = JsonContent.Create(new CreateEnrollmentRequest { ClassId = targetClass.Id, Seats = 0 })
        };
        httpRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(httpRequest);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task CreateEnrollment_ClassNotFound_ShouldReturn404()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user002@club.test");
        var token = GenerateValidToken(user);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/enrollments")
        {
            Content = JsonContent.Create(new CreateEnrollmentRequest { ClassId = 999999, Seats = 1 })
        };
        httpRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(httpRequest);
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task CreateEnrollment_CapacityExceeded_ShouldReturn409()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user002@club.test");
        var targetClass = await _dbContext.Classes.FirstAsync(c => c.StartsAt > DateTime.UtcNow && !c.IsCancelled);
        var token = GenerateValidToken(user);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/enrollments")
        {
            Content = JsonContent.Create(new CreateEnrollmentRequest { ClassId = targetClass.Id, Seats = 999 })
        };
        httpRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(httpRequest);
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.IsNotNull(error);
        Assert.AreEqual("capacity-exceeded", error.Reason);
    }

    [TestMethod]
    public async Task CreateEnrollment_PassExhausted_ShouldReturn409()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user020@club.test");
        var targetClass = await _dbContext.Classes
            .Where(c => c.StartsAt > DateTime.UtcNow && !c.IsCancelled)
            .OrderByDescending(c => c.StartsAt)
            .FirstAsync();
        var token = GenerateValidToken(user);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/enrollments")
        {
            Content = JsonContent.Create(new CreateEnrollmentRequest { ClassId = targetClass.Id, Seats = 1 })
        };
        httpRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(httpRequest);
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.IsNotNull(error);
        Assert.AreEqual("pass-exhausted", error.Reason);
    }

    [TestMethod]
    public async Task CreateEnrollment_PassExpired_ShouldReturn409()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user017@club.test");
        var targetClass = await _dbContext.Classes
            .Where(c => c.StartsAt > DateTime.UtcNow && !c.IsCancelled)
            .OrderByDescending(c => c.StartsAt)
            .FirstAsync();
        var token = GenerateValidToken(user);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/enrollments")
        {
            Content = JsonContent.Create(new CreateEnrollmentRequest { ClassId = targetClass.Id, Seats = 1 })
        };
        httpRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(httpRequest);
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.IsNotNull(error);
        Assert.AreEqual("pass-expired", error.Reason);
    }

    [TestMethod]
    public async Task CreateEnrollment_ClassStarted_ShouldReturn409()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user002@club.test");
        var targetClass = await _dbContext.Classes.FirstAsync(c => c.StartsAt <= DateTime.UtcNow);
        var token = GenerateValidToken(user);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/enrollments")
        {
            Content = JsonContent.Create(new CreateEnrollmentRequest { ClassId = targetClass.Id, Seats = 1 })
        };
        httpRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(httpRequest);
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.IsNotNull(error);
        Assert.AreEqual("class-started", error.Reason);
    }

    [TestMethod]
    public async Task CreateEnrollment_ClassCancelled_ShouldReturn409()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user002@club.test");
        var targetClass = await _dbContext.Classes.FirstAsync(c => c.IsCancelled && c.StartsAt > DateTime.UtcNow);
        var token = GenerateValidToken(user);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/enrollments")
        {
            Content = JsonContent.Create(new CreateEnrollmentRequest { ClassId = targetClass.Id, Seats = 1 })
        };
        httpRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(httpRequest);
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.IsNotNull(error);
        Assert.AreEqual("class-cancelled", error.Reason);
    }

    [TestMethod]
    public async Task ChangeEnrollment_DecreaseSeats_ShouldReturn200Ok()
    {
        var enrollment = await _dbContext.Enrollments
            .Include(e => e.Class)
            .FirstAsync(e => e.CancelledAt == null
                          && e.Status == "active"
                          && e.Seats >= 2
                          && e.Class.StartsAt > DateTime.UtcNow
                          && !e.Class.IsCancelled);

        var user = await _dbContext.Users.FirstAsync(u => u.Id == enrollment.UserId);
        var token = GenerateValidToken(user);
        var newSeats = enrollment.Seats - 1;

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"/enrollments/{enrollment.Id}/seats")
        {
            Content = JsonContent.Create(new ChangeEnrollmentRequest { Seats = newSeats })
        };
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(httpRequest);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<GetEnrollmentResponse>();
        Assert.IsNotNull(result);
        Assert.AreEqual(newSeats, result.Seats);
    }

    [TestMethod]
    public async Task ChangeEnrollment_InvalidSeats_ShouldReturn400()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user002@club.test");
        var enrollment = await _dbContext.Enrollments.FirstAsync(e => e.UserId == user.Id && e.CancelledAt == null);
        var token = GenerateValidToken(user);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"/enrollments/{enrollment.Id}/seats")
        {
            Content = JsonContent.Create(new ChangeEnrollmentRequest { Seats = 0 })
        };
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(httpRequest);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task ChangeEnrollment_EnrollmentNotFound_ShouldReturn404()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user002@club.test");
        var token = GenerateValidToken(user);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/enrollments/999999/seats")
        {
            Content = JsonContent.Create(new ChangeEnrollmentRequest { Seats = 1 })
        };
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(httpRequest);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task ChangeEnrollment_WrongUserId_ShouldReturn404()
    {
        var enrollment = await _dbContext.Enrollments.FirstAsync(e => e.CancelledAt == null);
        var anotherUser = await _dbContext.Users.FirstAsync(u => u.Id != enrollment.UserId);
        var token = GenerateValidToken(anotherUser);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"/enrollments/{enrollment.Id}/seats")
        {
            Content = JsonContent.Create(new ChangeEnrollmentRequest { Seats = enrollment.Seats })
        };
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(httpRequest);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task ChangeEnrollment_EnrollmentCancelled_ShouldReturn409()
    {
        var enrollment = await _dbContext.Enrollments.FirstAsync(e => e.CancelledAt != null);
        var user = await _dbContext.Users.FirstAsync(u => u.Id == enrollment.UserId);
        var token = GenerateValidToken(user);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"/enrollments/{enrollment.Id}/seats")
        {
            Content = JsonContent.Create(new ChangeEnrollmentRequest { Seats = enrollment.Seats })
        };
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(httpRequest);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.IsNotNull(error);
        Assert.AreEqual("enrollment-cancelled", error.Reason);
    }

    [TestMethod]
    public async Task ChangeEnrollment_ClassCancelled_ShouldReturn409()
    {
        var enrollment = await _dbContext.Enrollments
            .Include(e => e.Class)
            .FirstAsync(e => e.CancelledAt == null
                          && e.Class.IsCancelled
                          && e.Class.StartsAt > DateTime.UtcNow);

        var user = await _dbContext.Users.FirstAsync(u => u.Id == enrollment.UserId);
        var token = GenerateValidToken(user);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"/enrollments/{enrollment.Id}/seats")
        {
            Content = JsonContent.Create(new ChangeEnrollmentRequest { Seats = enrollment.Seats })
        };
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(httpRequest);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.IsNotNull(error);
        Assert.AreEqual("class-cancelled", error.Reason);
    }

    [TestMethod]
    public async Task ChangeEnrollment_ClassStarted_ShouldReturn409()
    {
        var enrollment = await _dbContext.Enrollments
            .Include(e => e.Class)
            .FirstAsync(e => e.CancelledAt == null && e.Class.StartsAt <= DateTime.UtcNow);

        var user = await _dbContext.Users.FirstAsync(u => u.Id == enrollment.UserId);
        var token = GenerateValidToken(user);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"/enrollments/{enrollment.Id}/seats")
        {
            Content = JsonContent.Create(new ChangeEnrollmentRequest { Seats = enrollment.Seats })
        };
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(httpRequest);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.IsNotNull(error);
        Assert.AreEqual("class-started", error.Reason);
    }

    [TestMethod]
    public async Task ChangeEnrollment_CapacityExceeded_ShouldReturn409()
    {
        var enrollment = await _dbContext.Enrollments
            .Include(e => e.Class)
            .ThenInclude(c => c.Enrollments)
            .FirstAsync(e => e.CancelledAt == null
                          && e.Class.StartsAt > DateTime.UtcNow
                          && !e.Class.IsCancelled);

        var user = await _dbContext.Users.FirstAsync(u => u.Id == enrollment.UserId);
        var token = GenerateValidToken(user);

        var newSeats = enrollment.Class.Capacity + 100;

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"/enrollments/{enrollment.Id}/seats")
        {
            Content = JsonContent.Create(new ChangeEnrollmentRequest { Seats = newSeats })
        };
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(httpRequest);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.IsNotNull(error);
        Assert.AreEqual("capacity-exceeded", error.Reason);
    }

    [TestMethod]
    public async Task ChangeEnrollment_PassExhausted_ShouldReturn409()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user110@club.test");
        var enrollment = await _dbContext.Enrollments
            .Include(e => e.Class)
            .FirstOrDefaultAsync(e => e.UserId == user.Id
                                   && e.CancelledAt == null
                                   && e.Class.StartsAt > DateTime.UtcNow
                                   && !e.Class.IsCancelled);

        var token = GenerateValidToken(user);
        var newSeats = enrollment.Seats + 1;

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"/enrollments/{enrollment.Id}/seats")
        {
            Content = JsonContent.Create(new ChangeEnrollmentRequest { Seats = newSeats })
        };
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(httpRequest);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.IsNotNull(error);
        Assert.AreEqual("pass-exhausted", error.Reason);
    }

    [TestMethod]
    public async Task ChangeEnrollment_PassExpired_ShouldReturn409()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user017@club.test");
        var enrollment = await _dbContext.Enrollments
            .Include(e => e.Class)
            .FirstOrDefaultAsync(e => e.UserId == user.Id
                                   && e.CancelledAt == null
                                   && e.Class.StartsAt > DateTime.UtcNow
                                   && !e.Class.IsCancelled);

        var token = GenerateValidToken(user);
        var newSeats = enrollment.Seats + 1;

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"/enrollments/{enrollment.Id}/seats")
        {
            Content = JsonContent.Create(new ChangeEnrollmentRequest { Seats = newSeats })
        };
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(httpRequest);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.IsNotNull(error);
        Assert.AreEqual("pass-expired", error.Reason);
    }

    [TestMethod]
    public async Task CancelEnrollment_MoreThanTwoHoursBeforeStart_ShouldReturn200AndRefundVisits()
    {
        var (user, pass, _, enrollment) = await ArrangeTestData(
            classStartsIn: TimeSpan.FromHours(3),
            isClassCancelled: false,
            enrollmentStatus: "active",
            seats: 2,
            initialPassVisits: 10);

        var token = GenerateValidToken(user);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/enrollments/{enrollment.Id}/cancel");
        request.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<CancelEnrollmentResponse>();
        Assert.IsNotNull(result);
        Assert.AreEqual(enrollment.Id, result.Id);
        Assert.AreEqual("cancelled", result.Status);
        Assert.AreEqual(2, result.RefundedVisits);

        var updatedPass = await _dbContext.Passes.AsNoTracking().FirstAsync(p => p.UserId == user.Id);
        Assert.AreEqual(12, updatedPass.RemainingVisits);
    }

    [TestMethod]
    public async Task CancelEnrollment_LessThanTwoHoursBeforeStart_ShouldReturn200AndNotRefundVisits()
    {
        var (user, pass, _, enrollment) = await ArrangeTestData(
            classStartsIn: TimeSpan.FromMinutes(90),
            isClassCancelled: false,
            enrollmentStatus: "active",
            seats: 2,
            initialPassVisits: 10);

        var token = GenerateValidToken(user);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/enrollments/{enrollment.Id}/cancel");
        request.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<CancelEnrollmentResponse>();
        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.RefundedVisits);

        var updatedPass = await _dbContext.Passes.AsNoTracking().FirstAsync(p => p.UserId == user.Id);
        Assert.AreEqual(10, updatedPass.RemainingVisits);
    }

    [TestMethod]
    public async Task CancelEnrollment_AlreadyCancelled_ShouldReturn200AndBeIdempotent()
    {
        var (user, pass, _, enrollment) = await ArrangeTestData(
            classStartsIn: TimeSpan.FromHours(3),
            isClassCancelled: false,
            enrollmentStatus: "cancelled",
            seats: 2,
            initialPassVisits: 10,
            refundedVisits: 2);

        var token = GenerateValidToken(user);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/enrollments/{enrollment.Id}/cancel");
        request.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<CancelEnrollmentResponse>();
        Assert.IsNotNull(result);
        Assert.AreEqual("cancelled", result.Status);
        Assert.AreEqual(2, result.RefundedVisits);

        var updatedPass = await _dbContext.Passes.AsNoTracking().FirstAsync(p => p.UserId == user.Id);
        Assert.AreEqual(10, updatedPass.RemainingVisits);
    }

    [TestMethod]
    public async Task CancelEnrollment_ClassStarted_ShouldReturn409()
    {
        var (user, _, _, enrollment) = await ArrangeTestData(
            classStartsIn: TimeSpan.FromHours(-1),
            isClassCancelled: false,
            enrollmentStatus: "active");

        var token = GenerateValidToken(user);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/enrollments/{enrollment.Id}/cancel");
        request.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.IsNotNull(error);
        Assert.AreEqual("class-started", error.Reason);
    }

    [TestMethod]
    public async Task CancelEnrollment_BelongsToAnotherUser_ShouldReturn404()
    {
        var (ownerUser, _, _, enrollment) = await ArrangeTestData(
            classStartsIn: TimeSpan.FromHours(3),
            isClassCancelled: false,
            enrollmentStatus: "active");

        var attackerUser = new User
        {
            Email = $"attacker_{Guid.NewGuid()}@test.com",
            PasswordHash = "hash",
            Role = "user",
            CreatedAt = DateTime.UtcNow
        };
        _dbContext.Users.Add(attackerUser);
        await _dbContext.SaveChangesAsync();

        var token = GenerateValidToken(attackerUser);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/enrollments/{enrollment.Id}/cancel");
        request.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task CancelEnrollment_ClassCancelledByClub_ShouldReturn200AndRefundVisits()
    {
        var (user, pass, _, enrollment) = await ArrangeTestData(
            classStartsIn: TimeSpan.FromHours(-1),
            isClassCancelled: true,
            enrollmentStatus: "active",
            seats: 2,
            initialPassVisits: 10);

        var token = GenerateValidToken(user);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/enrollments/{enrollment.Id}/cancel");
        request.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<CancelEnrollmentResponse>();
        Assert.IsNotNull(result);
        Assert.AreEqual(2, result.RefundedVisits);

        var updatedPass = await _dbContext.Passes.AsNoTracking().FirstAsync(p => p.UserId == user.Id);
        Assert.AreEqual(12, updatedPass.RemainingVisits);
    }

    

    [TestMethod]
    public async Task GetEnrollment_ActiveEnrollment_ReturnsOkWithCorrectData()
    {
        var enrollment = await _dbContext.Enrollments
            .Include(e => e.Class).ThenInclude(c => c.Room)
            .FirstAsync(e => e.Status == "active");

        var user = await _dbContext.Users.FirstAsync(u => u.Id == enrollment.UserId);
        var token = GenerateValidToken(user);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/enrollments/{enrollment.Id}");
        request.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<GetEnrollmentResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.IsNotNull(result);
        Assert.AreEqual(enrollment.Id, result.Id);
        Assert.AreEqual("active", result.Status);
        Assert.AreEqual(enrollment.Seats, result.Seats);
        Assert.AreEqual(enrollment.Class.Id, result.Class.Id);
        Assert.AreEqual(enrollment.Class.Room.Id, result.Class.Room.Id);
        Assert.AreEqual(enrollment.Class.Room.Name, result.Class.Room.Name);
    }

    [TestMethod]
    public async Task GetEnrollment_CancelledEnrollment_ReturnsOkWithCancelledStatus()
    {
        var enrollment = await _dbContext.Enrollments
            .Include(e => e.Class)
            .FirstAsync(e => e.Status == "cancelled");

        var user = await _dbContext.Users.FirstAsync(u => u.Id == enrollment.UserId);
        var token = GenerateValidToken(user);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/enrollments/{enrollment.Id}");
        request.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<GetEnrollmentResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.IsNotNull(result);
        Assert.AreEqual("cancelled", result.Status);
        Assert.AreEqual(enrollment.Seats, result.Seats);
    }

    [TestMethod]
    public async Task GetEnrollment_NotFound_Returns404()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user001@club.test");
        var token = GenerateValidToken(user);

        var request = new HttpRequestMessage(HttpMethod.Get, "/enrollments/999999999");
        request.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task GetEnrollment_BelongsToAnotherUser_Returns404()
    {
        var enrollment = await _dbContext.Enrollments.FirstAsync(e => e.Status == "active");

        var otherUser = await _dbContext.Users.FirstAsync(u => u.Id != enrollment.UserId);
        var token = GenerateValidToken(otherUser);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/enrollments/{enrollment.Id}");
        request.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task GetEnrollment_ClassCancelledByClub_ReturnsOk()
    {
        var enrollment = await _dbContext.Enrollments
            .Include(e => e.Class)
            .FirstAsync(e => e.Class.IsCancelled);

        var user = await _dbContext.Users.FirstAsync(u => u.Id == enrollment.UserId);
        var token = GenerateValidToken(user);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/enrollments/{enrollment.Id}");
        request.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<GetEnrollmentResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.IsNotNull(result);
        Assert.IsTrue(result.Class.IsCanceled);
    }

    [TestMethod]
    public async Task JoinWaitlist_Success_Returns201AndCorrectPosition()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user010@club.test");

        var targetClass = await _dbContext.Classes
            .Where(c => !c.IsCancelled && c.StartsAt > DateTime.UtcNow)
            .OrderBy(c => c.Id)
            .FirstAsync();

        var token = GenerateValidToken(user);
        var requestContent = JsonContent.Create(new JoinWaitlistRequest { ClassId = targetClass.Id, Seats = 100 });

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/enrollments/waitlist") { Content = requestContent };
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");
        httpRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await _client.SendAsync(httpRequest);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<JoinWaitlistResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.IsNotNull(result);
        Assert.AreEqual(targetClass.Id, result.ClassId);
        Assert.AreEqual(100, result.Seats);
        Assert.IsTrue(result.Position > 0);
    }

    [TestMethod]
    public async Task JoinWaitlist_IdempotentRequest_ReturnsSameResult()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user011@club.test");

        var targetClass = await _dbContext.Classes
            .Where(c => !c.IsCancelled && c.StartsAt > DateTime.UtcNow)
            .OrderBy(c => c.Id)
            .FirstAsync();

        var token = GenerateValidToken(user);
        var idempotencyKey = Guid.NewGuid().ToString();
        var requestContent = JsonContent.Create(new JoinWaitlistRequest { ClassId = targetClass.Id, Seats = 100 });

        var httpRequest1 = new HttpRequestMessage(HttpMethod.Post, "/enrollments/waitlist") { Content = requestContent };
        httpRequest1.Headers.Add("Authorization", $"Bearer {token}");
        httpRequest1.Headers.Add("Idempotency-Key", idempotencyKey);

        var response1 = await _client.SendAsync(httpRequest1);
        var result1 = await response1.Content.ReadFromJsonAsync<JoinWaitlistResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var httpRequest2 = new HttpRequestMessage(HttpMethod.Post, "/enrollments/waitlist") { Content = requestContent };
        httpRequest2.Headers.Add("Authorization", $"Bearer {token}");
        httpRequest2.Headers.Add("Idempotency-Key", idempotencyKey);

        var response2 = await _client.SendAsync(httpRequest2);
        var result2 = await response2.Content.ReadFromJsonAsync<JoinWaitlistResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.AreEqual(HttpStatusCode.Created, response2.StatusCode);
        Assert.AreEqual(result1.Id, result2.Id);
        Assert.AreEqual(result1.Position, result2.Position);
    }

    [TestMethod]
    public async Task JoinWaitlist_ClassCancelled_ShouldReturn409()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user012@club.test");

        var cancelledClass = await _dbContext.Classes.FirstAsync(c => c.IsCancelled);

        var token = GenerateValidToken(user);
        var requestContent = JsonContent.Create(new JoinWaitlistRequest { ClassId = cancelledClass.Id, Seats = 1 });

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/enrollments/waitlist") { Content = requestContent };
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");
        httpRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await _client.SendAsync(httpRequest);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.IsNotNull(error);
        Assert.AreEqual("class-cancelled", error.Reason);
    }

    [TestMethod]
    public async Task JoinWaitlist_ClassStarted_ShouldReturn409()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user013@club.test");

        var startedClass = await _dbContext.Classes
            .Where(c => !c.IsCancelled && c.StartsAt <= DateTime.UtcNow)
            .FirstAsync();

        var token = GenerateValidToken(user);
        var requestContent = JsonContent.Create(new JoinWaitlistRequest { ClassId = startedClass.Id, Seats = 1 });

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/enrollments/waitlist") { Content = requestContent };
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");
        httpRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await _client.SendAsync(httpRequest);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.IsNotNull(error);
        Assert.AreEqual("class-started", error.Reason);
    }

    [TestMethod]
    public async Task JoinWaitlist_AlreadyWaiting_ShouldReturn409()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user005@club.test");

        var waitlistEntry = await _dbContext.Waitlists.FirstAsync(w => w.UserId == user.Id && w.Status == "active");
        var targetClassId = waitlistEntry.ClassId;

        var token = GenerateValidToken(user);
        var requestContent = JsonContent.Create(new JoinWaitlistRequest { ClassId = targetClassId, Seats = 1 });

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/enrollments/waitlist") { Content = requestContent };
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");
        httpRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await _client.SendAsync(httpRequest);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.IsNotNull(error);
        Assert.AreEqual("already-waiting", error.Reason);
    }

    [TestMethod]
    public async Task JoinWaitlist_ClassNotFound_ShouldReturn404()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user015@club.test");

        var token = GenerateValidToken(user);
        var requestContent = JsonContent.Create(new JoinWaitlistRequest { ClassId = 999999999, Seats = 1 });

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/enrollments/waitlist") { Content = requestContent };
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");
        httpRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await _client.SendAsync(httpRequest);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task CancelWaitlist_ActiveWaitlist_Returns200AndUpdatesStatus()
    {
        var waitlist = await _dbContext.Waitlists.FirstAsync(w => w.Status == "active");
        var user = await _dbContext.Users.FirstAsync(u => u.Id == waitlist.UserId);
        var token = GenerateValidToken(user);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"/enrollments/waitlist/{waitlist.Id}/cancel");
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(httpRequest);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<CancelWaitlistResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.IsNotNull(result);
        Assert.AreEqual(waitlist.Id, result.Id);
        Assert.AreEqual("cancelled", result.Status);

        var updatedWaitlist = await _dbContext.Waitlists.AsNoTracking().FirstAsync(w => w.Id == waitlist.Id);
        Assert.AreEqual("cancelled", updatedWaitlist.Status);
    }

    [TestMethod]
    public async Task CancelWaitlist_AlreadyCancelled_Returns200AndIsIdempotent()
    {
        var waitlist = await _dbContext.Waitlists.FirstAsync(w => w.Status == "cancelled");
        var user = await _dbContext.Users.FirstAsync(u => u.Id == waitlist.UserId);
        var token = GenerateValidToken(user);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"/enrollments/waitlist/{waitlist.Id}/cancel");
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(httpRequest);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<CancelWaitlistResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.IsNotNull(result);
        Assert.AreEqual(waitlist.Id, result.Id);
        Assert.AreEqual("cancelled", result.Status);
    }

    [TestMethod]
    public async Task CancelWaitlist_NotFound_Returns404()
    {
        var user = await _dbContext.Users.FirstAsync(u => u.Email == "user001@club.test");
        var token = GenerateValidToken(user);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/enrollments/waitlist/999999999/cancel");
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(httpRequest);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task CancelWaitlist_BelongsToAnotherUser_Returns404()
    {
        var waitlist = await _dbContext.Waitlists.FirstAsync(w => w.Status == "active");

        var attackerUser = await _dbContext.Users.FirstAsync(u => u.Id != waitlist.UserId);
        var token = GenerateValidToken(attackerUser);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"/enrollments/waitlist/{waitlist.Id}/cancel");
        httpRequest.Headers.Add("Authorization", $"Bearer {token}");

        var response = await _client.SendAsync(httpRequest);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<(User user, Pass pass, Class targetClass, Enrollment enrollment)> ArrangeTestData(
        TimeSpan classStartsIn,
        bool isClassCancelled,
        string enrollmentStatus,
        int seats = 2,
        int initialPassVisits = 10,
        int refundedVisits = 0)
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];

        var user = new User
        {
            Email = $"testuser_{uniqueId}@test.com",
            PasswordHash = "hash",
            Role = "user",
            CreatedAt = DateTime.UtcNow
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();

        var pass = new Pass
        {
            UserId = user.Id,
            RemainingVisits = initialPassVisits,
            ValidUntil = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30))
        };
        _dbContext.Passes.Add(pass);

        var room = new Room { Name = $"TestRoom_{uniqueId}" };
        _dbContext.Rooms.Add(room);
        await _dbContext.SaveChangesAsync();

        var targetClass = new Class
        {
            RoomId = room.Id,
            Title = $"TestClass_{uniqueId}",
            StartsAt = DateTime.UtcNow.Add(classStartsIn),
            DurationMinutes = 60,
            Capacity = 20,
            IsCancelled = isClassCancelled
        };
        _dbContext.Classes.Add(targetClass);
        await _dbContext.SaveChangesAsync();

        var enrollment = new Enrollment
        {
            UserId = user.Id,
            ClassId = targetClass.Id,
            Seats = seats,
            Status = enrollmentStatus,
            CreatedAt = DateTime.UtcNow.AddHours(-5),
            CancelledAt = enrollmentStatus == "cancelled" ? DateTime.UtcNow.AddHours(-1) : null,
        };
        _dbContext.Enrollments.Add(enrollment);
        await _dbContext.SaveChangesAsync();

        return (user, pass, targetClass, enrollment);
    }

    private string GenerateValidToken(User user)
    {
        var response = _tokenService.Create(user);
        return response.AccessToken;
    }

    private class ErrorResponse
    {
        public string Reason { get; set; }
        public string Message { get; set; }
    }
}
