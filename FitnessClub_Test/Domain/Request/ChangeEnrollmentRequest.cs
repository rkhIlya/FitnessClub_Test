using System.ComponentModel.DataAnnotations;

namespace Domain.Request;
public class ChangeEnrollmentRequest
{
    [Required]
    [Range(1, int.MaxValue)]
    public int? Seats { get; set; }
}
