namespace Domain.Response;
public class CancelEnrollmentResponse
{
    public long Id { get; set; }

    public string Status { get; set; }

    public int RefundedVisits { get; set; }
}
