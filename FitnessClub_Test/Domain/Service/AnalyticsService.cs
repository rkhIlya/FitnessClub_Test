using DataAccessLayer.UnitOfWork;
using Domain.Interface;
using Domain.Request;
using Domain.Response;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;

namespace Domain.Service;
public class AnalyticsService : IAnalyticsService
{
    private readonly IUnitOfWork _unitOfWork;

    public AnalyticsService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<LoadAnalyticsResponse> LoadAnalyticsAsync(LoadAnalyticsRequest request, CancellationToken ct)
    {
        var analyticsInfo = await _unitOfWork.Class.GetAnalyticsInfoAsync(request.From, request.To, request.RoomId, ct);

        var result = new LoadAnalyticsResponse()
        {
            Items = analyticsInfo.Select(x => new AnalyticsItem()
            {
                Date = x.Date,
                Room = new AnalyticsRoom()
                {
                    Id = x.Room.Id,
                    Name = x.Room.Name
                },
                BookedSeats = x.BookedSeats,
                ClassesCount = x.ClassesCount,
                TotalCapacity = x.TotalCapacity
            }).ToList()
        };

        return result;
    }
}
