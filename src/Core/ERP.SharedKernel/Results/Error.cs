namespace ERP.SharedKernel.Results;

/// <summary>
/// ERROR — Represents a business error in a structured way.
///
/// WHY NOT JUST THROW EXCEPTIONS?
/// ================================
/// Exceptions are for EXCEPTIONAL situations (database down, network failure).
/// Business rule violations are EXPECTED (e.g., "insufficient stock", "invalid job card").
///
/// Using Result + Error instead of exceptions:
/// - Is more explicit (method signature tells you it can fail)
/// - Is faster (exceptions are expensive to throw)
/// - Forces the caller to handle the error case
/// - Makes API responses cleaner (400 Bad Request with error details)
///
/// INTERVIEW TIP: "We use the Result pattern instead of exceptions for
/// business rule violations. Exceptions are reserved for truly unexpected failures."
/// </summary>
public sealed record Error(string Code, string Message)
{
    /// <summary>Sentinel value meaning "no error"</summary>
    public static readonly Error None = new(string.Empty, string.Empty);

    /// <summary>Generic null value error</summary>
    public static readonly Error NullValue = new("Error.NullValue", "A required value was null.");

    /// <summary>Generic not found error</summary>
    public static Error NotFound(string entityName, object id)
        => new($"{entityName}.NotFound", $"{entityName} with Id '{id}' was not found.");

    /// <summary>Generic validation error</summary>
    public static Error Validation(string code, string message)
        => new(code, message);

    /// <summary>Generic conflict/duplicate error</summary>
    public static Error Conflict(string code, string message)
        => new(code, message);

    /// <summary>Authorization error</summary>
    public static Error Unauthorized(string message = "You are not authorized to perform this action.")
        => new("Error.Unauthorized", message);
}
