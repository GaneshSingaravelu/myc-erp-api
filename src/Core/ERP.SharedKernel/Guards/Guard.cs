namespace ERP.SharedKernel.Guards;

/// <summary>
/// GUARD CLAUSES — Validate method inputs at the boundary.
///
/// WHAT IS A GUARD CLAUSE?
/// ========================
/// A guard clause is a check at the beginning of a method that validates inputs
/// and throws immediately if they're invalid. This prevents bad data from
/// propagating deeper into the system.
///
/// BEFORE (nested ifs — hard to read):
///   if (name != null) {
///       if (name.Length > 0) {
///           // actual logic buried here
///       }
///   }
///
/// AFTER (guard clauses — clean and flat):
///   Guard.AgainstNullOrEmpty(name, nameof(name));
///   // actual logic starts immediately
///
/// DESIGN PATTERN: Guard Clause / Fail Fast
/// </summary>
public static class Guard
{
    public static void AgainstNull(object? value, string paramName)
    {
        if (value is null)
            throw new ArgumentNullException(paramName, $"{paramName} cannot be null.");
    }

    public static void AgainstNullOrEmpty(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{paramName} cannot be null or empty.", paramName);
    }

    public static void AgainstNegative(int value, string paramName)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(paramName, $"{paramName} cannot be negative. Got: {value}");
    }

    public static void AgainstNegative(decimal value, string paramName)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(paramName, $"{paramName} cannot be negative. Got: {value}");
    }

    public static void AgainstZeroOrNegative(int value, string paramName)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(paramName, $"{paramName} must be positive. Got: {value}");
    }

    public static void AgainstOutOfRange(int value, int min, int max, string paramName)
    {
        if (value < min || value > max)
            throw new ArgumentOutOfRangeException(paramName, $"{paramName} must be between {min} and {max}. Got: {value}");
    }
}
