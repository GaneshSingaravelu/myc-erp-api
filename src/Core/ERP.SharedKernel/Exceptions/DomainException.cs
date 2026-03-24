namespace ERP.SharedKernel.Exceptions;

/// <summary>
/// DOMAIN EXCEPTION — For truly exceptional domain violations.
///
/// WHEN TO USE EXCEPTION vs RESULT:
/// ==================================
/// Result: Expected business failures (validation, not found, business rule violation)
///         → Returns 400/404 to the client
///
/// DomainException: Programming errors or invariant violations that should NEVER happen
///                  if the code is correct → Returns 500 (bug in our code)
///
/// Example:
/// - "Stock insufficient" → Result.Failure (expected, user can fix it)
/// - "Entity has negative Id" → DomainException (should never happen, it's a bug)
/// </summary>
public class DomainException : Exception
{
    public DomainException(string message) : base(message) { }
    public DomainException(string message, Exception innerException) : base(message, innerException) { }
}
