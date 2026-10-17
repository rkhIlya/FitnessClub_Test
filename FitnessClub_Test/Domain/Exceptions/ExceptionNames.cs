namespace Domain.Exceptions;
public static class ExceptionNames
{
    public const string CapacityExceeded = "capacity-exceeded";

    public const string PassExhausted = "pass-exhausted";

    public const string PassExpired = "pass-expired";

    public const string ClassStarted = "class-started";

    public const string ClassCancelled = "class-cancelled";

    public const string ClassIsNotExists = "Class is not exists";

    public const string EnrollmentIsNotExists = "enrollment is not exists";

    public const string EnrollmentCancelled = "enrollment-cancelled";

    public const string AlreadyWaiting = "already-waiting";

    public const string SeatsAvailable = "seats-available";

    public const string WaitlistIsNotExists = "Waitlist is not exists";
}
