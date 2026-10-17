using System.ComponentModel.DataAnnotations;

namespace Domain.Request;

public class CreateEnrollmentRequest
{
    [Required]
    [Range(1, int.MaxValue)]
    public long? ClassId { get; set; }

    [Required]
    [Range(1, int.MaxValue)]
    public int? Seats { get; set; }
}
