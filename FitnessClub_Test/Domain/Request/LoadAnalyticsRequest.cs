using System.ComponentModel.DataAnnotations;

namespace Domain.Request;
public class LoadAnalyticsRequest : IValidatableObject
{
    [Required]
    public DateOnly From {  get; set; }

    [Required]
    public DateOnly To { get; set; }

    public long? RoomId{ get; set; }

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
