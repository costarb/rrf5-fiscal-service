namespace Rrf5.Fiscal.Application.Errors;

/// <summary>How an error is surfaced; the API maps each category to one HTTP status.</summary>
public enum ErrorCategory
{
    InvalidRequest,
    Unauthenticated,
    Forbidden,
    NotFound,
    Conflict,
    Unprocessable,
    RateLimited,
    Unavailable,
    Internal,
}

/// <summary>
/// A stable domain error code (FF-012). Codes are part of the public contract:
/// clients branch on them, so an existing code is never renamed or reused.
/// </summary>
public sealed record ErrorCode(string Value, ErrorCategory Category, string Title)
{
    public override string ToString() => Value;
}

/// <summary>The closed catalog of error codes.</summary>
public static class ErrorCodes
{
    public static readonly ErrorCode RequestInvalid = new("request.invalid", ErrorCategory.InvalidRequest, "The request is invalid.");
    public static readonly ErrorCode IdempotencyKeyInvalid = new("request.idempotency_key_invalid", ErrorCategory.InvalidRequest, "The Idempotency-Key header is missing or invalid.");
    public static readonly ErrorCode Unauthenticated = new("auth.unauthenticated", ErrorCategory.Unauthenticated, "Authentication is required.");
    public static readonly ErrorCode Forbidden = new("auth.forbidden", ErrorCategory.Forbidden, "The operation is not allowed.");
    public static readonly ErrorCode ScopeMissing = new("auth.scope_missing", ErrorCategory.Forbidden, "The token lacks the required scope.");
    public static readonly ErrorCode FourEyes = new("rules.four_eyes", ErrorCategory.Forbidden, "The author cannot approve, or the approver lacks the approval scope.");
    public static readonly ErrorCode NotFound = new("resource.not_found", ErrorCategory.NotFound, "The resource was not found.");
    public static readonly ErrorCode MethodNotAllowed = new("request.method_not_allowed", ErrorCategory.InvalidRequest, "The method is not allowed for this resource.");
    public static readonly ErrorCode Conflict = new("resource.conflict", ErrorCategory.Conflict, "The resource conflicts with an existing one.");
    public static readonly ErrorCode IdempotencyConflict = new("idempotency.conflict", ErrorCategory.Conflict, "The Idempotency-Key was already used with a different request.");
    public static readonly ErrorCode Immutable = new("state.immutable", ErrorCategory.Conflict, "The resource can no longer be changed.");
    public static readonly ErrorCode InvalidTransition = new("state.invalid_transition", ErrorCategory.Conflict, "The state transition is not allowed.");
    public static readonly ErrorCode SeriesExhausted = new("series.exhausted", ErrorCategory.Conflict, "The series has no number left in its range.");
    public static readonly ErrorCode CertificateInvalid = new("certificate.invalid", ErrorCategory.Unprocessable, "The certificate is not a usable A1.");
    public static readonly ErrorCode CertificateExpired = new("certificate.outside_validity", ErrorCategory.Unprocessable, "The certificate is expired or not yet valid.");
    public static readonly ErrorCode CertificateHolderMismatch = new("certificate.holder_mismatch", ErrorCategory.Unprocessable, "The certificate CNPJ-Base differs from the establishment CNPJ-Base (MOC rejection 213).");
    public static readonly ErrorCode UnsupportedGroup = new("rules.unsupported_group", ErrorCategory.Unprocessable, "The rule asks for a layout group the generator does not build.");
    public static readonly ErrorCode RulePayloadInvalid = new("rules.payload_invalid", ErrorCategory.Unprocessable, "The rule payload is inconsistent with the layout.");
    public static readonly ErrorCode RuleNotInForce = new("rules.not_in_force", ErrorCategory.NotFound, "No approved rule version is in force for these dimensions and date.");
    public static readonly ErrorCode RuleCrtMismatch = new("rules.crt_mismatch", ErrorCategory.Unprocessable, "The rule CRT differs from the establishment CRT.");
    public static readonly ErrorCode ValidityOverlap = new("rules.validity_overlap", ErrorCategory.Conflict, "The validity overlaps an approved version of the same rule.");
    public static readonly ErrorCode ConstraintViolated = new("validation.constraint", ErrorCategory.Unprocessable, "The request violates a consistency rule.");
    public static readonly ErrorCode RateLimited = new("rate_limited", ErrorCategory.RateLimited, "Too many requests.");
    public static readonly ErrorCode Unavailable = new("dependency.unavailable", ErrorCategory.Unavailable, "A required dependency is unavailable.");
    public static readonly ErrorCode Internal = new("internal.error", ErrorCategory.Internal, "An unexpected error occurred.");

    /// <summary>Every code, for uniqueness tests and the contract.</summary>
    public static IReadOnlyList<ErrorCode> All { get; } =
    [
        RequestInvalid, IdempotencyKeyInvalid, Unauthenticated, Forbidden, ScopeMissing, FourEyes, NotFound,
        MethodNotAllowed, Conflict, IdempotencyConflict, Immutable, InvalidTransition, SeriesExhausted, CertificateInvalid, CertificateExpired, CertificateHolderMismatch, UnsupportedGroup, RulePayloadInvalid, RuleNotInForce, RuleCrtMismatch, ValidityOverlap, ConstraintViolated,
        RateLimited, Unavailable, Internal,
    ];
}

/// <summary>
/// A failure with a stable code. <see cref="Exception.Message"/> is a fixed,
/// non-sensitive sentence: it is returned to clients and written to logs, so it
/// never carries request values, secrets or document data.
/// </summary>
public sealed class DomainException : Exception
{
    public DomainException(ErrorCode code, string? safeDetail = null, Exception? innerException = null)
        : base(safeDetail ?? code?.Title, innerException)
    {
        ArgumentNullException.ThrowIfNull(code);
        Code = code;
    }

    public DomainException()
        : this(ErrorCodes.Internal)
    {
    }

    public DomainException(string message)
        : this(ErrorCodes.Internal, message)
    {
    }

    public DomainException(string message, Exception innerException)
        : this(ErrorCodes.Internal, message, innerException)
    {
    }

    public ErrorCode Code { get; }
}
