namespace Domain.Exceptions;
public class NotFoundException : Exception
{
    public NotFoundException(string detail)
    {
        Detail = detail;
    }

    public string Detail { get; set; }
}
