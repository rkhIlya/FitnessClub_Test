namespace Domain.Response;
public class CreateEnrollmentResponse
{
    public long Id { get; set; }

    public long ClassId { get; set; }

    public int Seats { get; set; }

    public string Status { get; set; }

    public DateTime CreatedAt { get; set; }
}
