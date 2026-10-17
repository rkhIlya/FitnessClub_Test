namespace Domain.Response;
public class GetScheduleResponse
{
    public List<ScheduleItem> Items { get; set; }

    public string ContinuationToken { get; set; }
}

public class ScheduleItem
{
    public long ClassId { get; set; }

    public string Title { get; set; }

    public  DateTime StartsAt { get; set; }

    public int DurationMinutes { get; set; }

    public ScheduleRoom Room { get; set; }

    public int Capacity { get; set; }

    public int FreeSeats { get; set; }

    public int MySeats { get; set; }
}

public class ScheduleRoom
{
    public long Id { get; set; }

    public string Name { get; set; }
}

