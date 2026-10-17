namespace DataAccessLayer.Dto;
public class ClassEnrollmentDto
{
    public long ClassId { get; set; }

    public int TotalBookedSeats { get; set; }

    public int UserBookedSeats { get; set; }
}
