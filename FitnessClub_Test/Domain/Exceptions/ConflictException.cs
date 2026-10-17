namespace Domain.Exceptions;
public class ConflictException : Exception
{
    public ConflictException(string reason)
    {
        Reason = reason;
    }

    public string Reason { get; set; }
}
