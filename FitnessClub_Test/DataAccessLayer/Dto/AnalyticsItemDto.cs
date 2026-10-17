namespace DataAccessLayer.Dto;
public class AnalyticsItemDto
{
    public DateOnly Date { get; set; }

    public RoomDto Room { get; set; }

    public int ClassesCount { get; set; }

    public int TotalCapacity { get; set; }

    public int BookedSeats { get; set; }
}

public class RoomDto
{
    public long Id { get; set; }

    public string Name { get; set; }
}
