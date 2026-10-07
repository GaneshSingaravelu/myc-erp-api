using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ERP.Persistence.Conversions;

/// <summary>
/// UTC DATETIME ENFORCEMENT — every persisted <see cref="DateTime"/> is a UTC instant.
///
/// THE PROBLEM THIS SOLVES
/// =======================
/// SQL Server's <c>datetime2</c> stores no zone, so EF materializes it with
/// <see cref="DateTimeKind.Unspecified"/>. The clock value is correct — the application only
/// ever writes <c>DateTime.UtcNow</c> — but the <c>Kind</c> is a lie, and nothing complains
/// because <c>&gt;</c> and <c>&lt;</c> compare ticks and ignore <c>Kind</c> entirely. The
/// damage only appears where something CONVERTS:
///
///   * <c>ToUniversalTime()</c> on an Unspecified value applies the SERVER's local offset,
///     shifting the instant by hours;
///   * JSON serialization omits the <c>Z</c>, and JavaScript then parses an offset-less
///     date-time as LOCAL time, shifting it again on the client;
///   * PostgreSQL's <c>timestamp with time zone</c> requires <c>Kind=Utc</c> and REJECTS
///     Unspecified, so the same model that works on SQL Server would fail there.
///
/// Restoring the correct <c>Kind</c> at the persistence boundary fixes all three at once:
/// there is then exactly one place that decides what a stored timestamp means.
///
/// WHY FAIL CLOSED ON WRITE
/// ========================
/// A non-UTC value reaching persistence is a VIOLATED INVARIANT, not an input to be fixed up.
/// The two "helpful" alternatives were both rejected deliberately:
///
///   * <c>ToUniversalTime()</c> — measured during the P1-h design review to shift an
///     Unspecified <c>2026-06-01T12:00:00</c> to <c>2026-06-01T06:30:00Z</c>, i.e. silently
///     by the server's offset. It cannot know whether the value was local or already UTC, so
///     it guesses, and a wrong guess is invisible.
///   * <c>SpecifyKind(v, Utc)</c> — never shifts, but silently RELABELS a genuinely local
///     value as UTC, which is the same bug with the error moved later.
///
/// Throwing surfaces the defect at the write that caused it. Every write path in this codebase
/// is <c>DateTime.UtcNow</c> and no request DTO carries a <c>DateTime</c>, so in correct code
/// this never fires; when it does fire, it has found something real.
///
/// WHY A CONVENTION RATHER THAN PER-PROPERTY MAPPING
/// ================================================
/// Applied through <c>ConfigureConventions</c> in both DbContexts, this covers every mapped
/// <see cref="DateTime"/> property — including ones added later. Configuring ~39 properties by
/// hand would make OMISSION the default failure mode, and a timestamp silently left out is
/// exactly the bug being fixed.
///
/// WHAT IT DOES NOT TOUCH
/// ======================
/// <see cref="DateOnly"/> is a different CLR type and is not matched by a
/// <c>Properties&lt;DateTime&gt;()</c> convention, so <c>WorkOrder.DueDate</c> (P1-g) keeps its
/// <c>date</c> column and gains no converter. Store types are unchanged on both providers:
/// <c>datetime2</c> on SQL Server, <c>timestamp with time zone</c> on PostgreSQL. This slice
/// is therefore migration-free by construction, and tests assert it.
/// </summary>
public static class UtcDateTime
{
    /// <summary>
    /// Write guard. Returns a UTC value unchanged; throws for anything else.
    ///
    /// <para>Referenced from a value-converter expression tree, so it must stay a plain static
    /// method with no closure over context.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="value"/> is <see cref="DateTimeKind.Unspecified"/> or
    /// <see cref="DateTimeKind.Local"/>.
    /// </exception>
    public static DateTime ToStore(DateTime value) =>
        value.Kind == DateTimeKind.Utc
            ? value
            : throw new InvalidOperationException(
                $"Refusing to persist a DateTime with Kind={value.Kind} ({value:O}). " +
                "Every persisted DateTime in this application is a UTC instant. " +
                "Produce the value with DateTime.UtcNow, or convert it explicitly at the " +
                "point where its real time zone is known — this layer will not guess, " +
                "because guessing shifts the instant silently. " +
                "A calendar date with no time of day belongs in a DateOnly property instead.");

    /// <summary>
    /// Read side. The stored clock value is already UTC, so only the missing
    /// <see cref="DateTimeKind"/> label is restored. Ticks are never altered.
    /// </summary>
    public static DateTime FromStore(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>Nullable write guard. <c>null</c> passes through untouched.</summary>
    public static DateTime? ToStoreNullable(DateTime? value) => value.HasValue ? ToStore(value.Value) : null;

    /// <summary>Nullable read side. <c>null</c> stays <c>null</c>.</summary>
    public static DateTime? FromStoreNullable(DateTime? value) => value.HasValue ? FromStore(value.Value) : null;
}

/// <summary>
/// Value converter for non-nullable <see cref="DateTime"/>. Accepts UTC unchanged, refuses
/// anything else, and materializes with <see cref="DateTimeKind.Utc"/>.
/// See <see cref="UtcDateTime"/> for the reasoning.
/// </summary>
public sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
    public UtcDateTimeConverter()
        : base(v => UtcDateTime.ToStore(v), v => UtcDateTime.FromStore(v))
    {
    }
}

/// <summary>
/// Value converter for <see cref="Nullable{DateTime}"/>, on the same terms, with <c>null</c>
/// preserved in both directions.
/// </summary>
public sealed class UtcNullableDateTimeConverter : ValueConverter<DateTime?, DateTime?>
{
    public UtcNullableDateTimeConverter()
        : base(v => UtcDateTime.ToStoreNullable(v), v => UtcDateTime.FromStoreNullable(v))
    {
    }
}
