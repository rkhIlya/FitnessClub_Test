namespace Domain.Response;
public class GetEnrollmentResponse
{
    public long Id { get; set; }

    public string Status { get; set; }

    public int Seats { get; set; }

    public DateTime CreatedAt { get; set; }

    public EnrollmentClass Class { get; set; }

    public EnrollmentPass Pass { get; set; }
}

public class EnrollmentRoom
{
    public long Id { get; set; }

    public string Name { get; set; }
}

public class EnrollmentClass
{
    public long Id { get; set; }

    public string Title { get; set; }

    public DateTime StartsAt { get; set; }

    public int DurationMinutes { get; set; }

    public bool IsCanceled { get; set; }

    public EnrollmentRoom Room { get; set; }
}

public class EnrollmentPass
{
    public int RemainingVisits { get; set; }

    public DateOnly ValidUntil { get; set; }
}

