using System.ComponentModel.DataAnnotations;

namespace Domain.Request;
public class GetScheduleRequest : IValidatableObject
{
    [Required]
    public DateTime From { get; set; }

    [Required]
    public DateTime To { get; set; }

    public long? RoomId { get; set; }

    [Range(1, 100)]
    public int Limit { get; set; } = 20;

    public string? ContinuationToken { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (To < From)
        {
            yield return new ValidationResult(
                "Дата окончания не может быть раньше даты начала",
                new[] { nameof(To) }
            );
        }
    }
}
