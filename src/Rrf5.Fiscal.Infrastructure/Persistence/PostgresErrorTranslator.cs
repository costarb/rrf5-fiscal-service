using Npgsql;
using Rrf5.Fiscal.Application.Errors;

namespace Rrf5.Fiscal.Infrastructure.Persistence;

/// <summary>
/// Maps database failures to stable domain codes (T012). The database message is
/// not forwarded: the detail of the resulting <see cref="DomainException"/> is the
/// code's fixed title, so constraint names and values never reach clients.
/// </summary>
public static class PostgresErrorTranslator
{
    public static ErrorCode? Map(PostgresException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.SqlState switch
        {
            "RF001" or "RF002" or "RF005" => ErrorCodes.Immutable,
            "RF003" => ErrorCodes.InvalidTransition,
            "RF004" => ErrorCodes.FourEyes,
            "RF006" => ErrorCodes.ConstraintViolated,
            PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ExclusionViolation => ErrorCodes.Conflict,
            PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.CheckViolation
                or PostgresErrorCodes.NotNullViolation => ErrorCodes.ConstraintViolated,
            // RLS violation: the row belongs to another tenant or no tenant is set.
            // Report it as not found, never revealing that the row exists (spec F1-1).
            PostgresErrorCodes.InsufficientPrivilege => ErrorCodes.NotFound,
            PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected
                or PostgresErrorCodes.LockNotAvailable => ErrorCodes.Unavailable,
            _ => null,
        };
    }

    /// <summary>Returns a <see cref="DomainException"/> for mapped failures, otherwise null.</summary>
    public static DomainException? Translate(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres && Map(postgres) is { } code)
            {
                return new DomainException(code, innerException: postgres);
            }

            if (current is NpgsqlException { IsTransient: true } transient)
            {
                return new DomainException(ErrorCodes.Unavailable, innerException: transient);
            }
        }

        return null;
    }
}
