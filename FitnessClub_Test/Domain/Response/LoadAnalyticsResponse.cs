namespace Domain.Response;
public class LoadAnalyticsResponse
{
    public List<AnalyticsItem> Items { get; set; }
}

public class AnalyticsItem
{
    public DateOnly Date { get; set; }

    public AnalyticsRoom Room { get; set; }

    public int ClassesCount {  get; set; }

    public int TotalCapacity { get; set; }

    public int BookedSeats {  get; set; }
}

public class AnalyticsRoom
{
    public long Id { get; set; }

    public string Name { get; set; }
}
