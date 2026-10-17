namespace Domain.Interface;
public interface IDateTimeProvider
{
    DateTime UtcNow { get; }
}
