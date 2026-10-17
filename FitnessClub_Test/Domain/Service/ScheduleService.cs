using System.Text.Json;
using DataAccessLayer.UnitOfWork;
using Domain.Exceptions;
using Domain.Interface;
using Domain.Request;
using Domain.Response;

namespace Domain.Service;
public class ScheduleService : IScheduleService
{
    private readonly IUnitOfWork _unitOfWork;

    public ScheduleService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<GetScheduleResponse> GetScheduleAsync(GetScheduleRequest request, long userId, CancellationToken ct)
    {
        ContinuationTokenData? tokenData = null;
        if (!string.IsNullOrEmpty(request.ContinuationToken))
        {
            try
            {
                var json = Base64Decode(request.ContinuationToken);
                tokenData = JsonSerializer.Deserialize<ContinuationTokenData>(json);
            }
            catch
            {
                throw new InvalidContinuationTokenException();
            }
        }

        var (classes, hasMore) = await _unitOfWork.Class.GetClassesForScheduleAsync(
            request.From,
            request.To,
            request.RoomId,
            tokenData?.LastStartsAt,
            tokenData?.LastId,
            request.Limit,
            ct);

        if (classes.Count == 0)
        {
            return new GetScheduleResponse()
            {
                Items = new List<ScheduleItem>(),
                ContinuationToken = null
            };
        }

        var classIds = classes.Select(c => c.Id).ToList();
        var summaries = await _unitOfWork.Enrollment.GetEnrollmentSummariesForClassesAsync(classIds, userId, ct);

        var summaryDict = summaries.ToDictionary(s => s.ClassId);

        var items = classes.Select(c =>
        {
            summaryDict.TryGetValue(c.Id, out var summary);

            var totalBooked = summary?.TotalBookedSeats ?? 0;
            var myBooked = summary?.UserBookedSeats ?? 0;

            return new ScheduleItem
            {
                ClassId = c.Id,
                Title = c.Title,
                StartsAt = c.StartsAt,
                DurationMinutes = c.DurationMinutes,
                Room = new ScheduleRoom()
                {
                    Id = c.Room.Id,
                    Name = c.Room.Name
                },
                Capacity = c.Capacity,
                FreeSeats = Math.Max(0, c.Capacity - totalBooked),
                MySeats = myBooked
            };
        }).ToList();

        string? nextToken = null;
        if (hasMore)
        {
            var last = classes.Last();
            var nextTokenData = new ContinuationTokenData { LastStartsAt = last.StartsAt, LastId = last.Id };
            nextToken = Base64Encode(JsonSerializer.Serialize(nextTokenData));
        }

        return new GetScheduleResponse { Items = items, ContinuationToken = nextToken };
    }

    private static string Base64Encode(string plainText)
    {
        var plainTextBytes = System.Text.Encoding.UTF8.GetBytes(plainText);
        return Convert.ToBase64String(plainTextBytes);
    }

    private static string Base64Decode(string base64EncodedData)
    {
        var base64EncodedBytes = Convert.FromBase64String(base64EncodedData);
        return System.Text.Encoding.UTF8.GetString(base64EncodedBytes);
    }
}
