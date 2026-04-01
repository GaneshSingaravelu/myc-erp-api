using ERP.SharedKernel.ValueObjects;

namespace ERP.Production.Domain.ValueObjects;

/// <summary>
/// JOB CARD NUMBER — A structured identifier for tracking job cards through production.
///
/// FORMAT: YYMMDDN###S###R##
/// =========================
/// YY    = Year (2 digits)
/// MM    = Month (2 digits)
/// DD    = Day (2 digits)
/// N###  = Running number for the day (N001, N002, ...)
/// S###  = Split number (S000 = original, S001 = first split, ...)
/// R##   = Rework number (R00 = original, R01 = first rework, ...)
///
/// EXAMPLES:
/// =========
/// 260322N001S000R00  → 22-Mar-2026, job #1, original, no rework
/// 260322N001S001R00  → 22-Mar-2026, job #1, first split, no rework
/// 260322N001S000R01  → 22-Mar-2026, job #1, original, first rework
///
/// WHY A VALUE OBJECT?
/// ====================
/// The number itself IS the identity. Two JobCardNumber objects with the same
/// string are equal. It also encapsulates parsing, validation, and generation
/// of split/rework numbers so callers don't need to know the format.
///
/// BARCODE:
/// ========
/// This string is rendered as a Code 128 barcode using a barcode font in reports.
/// No barcode library (ZXing) needed — just apply the font.
/// </summary>
public class JobCardNumber : ValueObject
{
    /// <summary>The full formatted number (e.g., "260322N001S000R00")</summary>
    public string Value { get; }

    private JobCardNumber(string value)
    {
        Value = value;
    }

    /// <summary>
    /// Create a new job card number for an original (non-split, non-rework) job card.
    /// </summary>
    /// <param name="date">The date the job card is created</param>
    /// <param name="runningNumber">Running number for the day (1-999)</param>
    public static JobCardNumber Create(DateTime date, int runningNumber)
    {
        if (runningNumber is < 1 or > 999)
            throw new ArgumentOutOfRangeException(nameof(runningNumber), "Running number must be between 1 and 999.");

        var value = $"{date:yyMMdd}N{runningNumber:D3}S000R00";
        return new JobCardNumber(value);
    }

    /// <summary>
    /// Create a split job card number from an existing number.
    /// Increments the S### portion.
    /// </summary>
    /// <param name="splitNumber">Split sequence (1-999)</param>
    public JobCardNumber CreateSplit(int splitNumber)
    {
        if (splitNumber is < 1 or > 999)
            throw new ArgumentOutOfRangeException(nameof(splitNumber), "Split number must be between 1 and 999.");

        // Replace S### portion (characters at index 9-12)
        var baseNumber = Value[..9]; // YYMMDDN###
        var reworkPart = Value[13..]; // R##
        var value = $"{baseNumber}S{splitNumber:D3}{reworkPart}";
        return new JobCardNumber(value);
    }

    /// <summary>
    /// Create a rework job card number from an existing number.
    /// Increments the R## portion.
    /// </summary>
    /// <param name="reworkNumber">Rework sequence (1-99)</param>
    public JobCardNumber CreateRework(int reworkNumber)
    {
        if (reworkNumber is < 1 or > 99)
            throw new ArgumentOutOfRangeException(nameof(reworkNumber), "Rework number must be between 1 and 99.");

        // Replace R## portion (last 3 characters)
        var basePart = Value[..14]; // YYMMDDN###S###
        var value = $"{basePart}R{reworkNumber:D2}";
        return new JobCardNumber(value);
    }

    /// <summary>Parse a raw string back into a JobCardNumber (e.g., from database).</summary>
    public static JobCardNumber FromString(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 17)
            throw new ArgumentException("Job card number must be exactly 17 characters.", nameof(value));

        return new JobCardNumber(value);
    }

    /// <summary>Extract the running number portion (N###) as an integer.</summary>
    public int GetRunningNumber() => int.Parse(Value[7..10]);

    /// <summary>Extract the split number portion (S###) as an integer.</summary>
    public int GetSplitNumber() => int.Parse(Value[11..14]);

    /// <summary>Extract the rework number portion (R##) as an integer.</summary>
    public int GetReworkNumber() => int.Parse(Value[15..17]);

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() => Value;
}
