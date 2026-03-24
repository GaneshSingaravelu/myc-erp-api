namespace ERP.SharedKernel.ValueObjects;

/// <summary>
/// VALUE OBJECT — An object defined by its properties, not its identity.
///
/// ENTITY vs VALUE OBJECT (Common Interview Question!)
/// ====================================================
/// Entity: Has an Id. Two entities with same data but different Ids are DIFFERENT.
///         Example: Employee #101 and Employee #102 (both named "John") are different.
///
/// Value Object: Has NO Id. Two value objects with same data are EQUAL.
///               Example: Money(100, "USD") == Money(100, "USD") regardless of where they came from.
///
/// REAL-WORLD ERP EXAMPLES:
/// ========================
/// - Money(amount, currency) — $100 USD is $100 USD, no matter who holds it
/// - Address(street, city, state, zip) — same address details = same address
/// - JobCardNumber("260322N001S000R00") — the number itself IS the identity
/// - DateRange(start, end) — same dates = same range
///
/// WHY USE VALUE OBJECTS?
/// ======================
/// 1. Type safety: Money is not just a decimal. You can't accidentally add Money to Temperature.
/// 2. Validation: The constructor enforces rules (e.g., amount cannot be negative).
/// 3. Immutability: Once created, a value object cannot change. This prevents bugs.
/// 4. Self-documenting: Method signature "Pay(Money amount)" is clearer than "Pay(decimal amount)"
///
/// DESIGN PATTERN: Value Object (from Domain-Driven Design / DDD)
/// </summary>
public abstract class ValueObject
{
    /// <summary>
    /// Subclasses define which properties determine equality.
    /// Example: For Money, it's [Amount, Currency].
    /// </summary>
    protected abstract IEnumerable<object?> GetEqualityComponents();

    public override bool Equals(object? obj)
    {
        if (obj is null || obj.GetType() != GetType())
            return false;

        var other = (ValueObject)obj;
        return GetEqualityComponents().SequenceEqual(other.GetEqualityComponents());
    }

    public override int GetHashCode()
    {
        return GetEqualityComponents()
            .Select(x => x?.GetHashCode() ?? 0)
            .Aggregate((x, y) => x ^ y);
    }

    public static bool operator ==(ValueObject? left, ValueObject? right)
    {
        if (left is null && right is null) return true;
        if (left is null || right is null) return false;
        return left.Equals(right);
    }

    public static bool operator !=(ValueObject? left, ValueObject? right) => !(left == right);
}
