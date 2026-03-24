namespace ERP.SharedKernel.Results;

/// <summary>
/// RESULT PATTERN — A method returns either a success value OR an error, never both.
///
/// WHY THE RESULT PATTERN? (Interview Question!)
/// ===============================================
/// Instead of:
///   public JobCard CreateJobCard(...) { throw new Exception("Stock insufficient"); }
///
/// We do:
///   public Result&lt;JobCard&gt; CreateJobCard(...) { return Result.Failure&lt;JobCard&gt;(StockErrors.Insufficient); }
///
/// Benefits:
/// 1. The method signature TELLS you it can fail (Result&lt;T&gt; vs T)
/// 2. The compiler forces you to check IsSuccess before accessing Value
/// 3. No try/catch blocks littered everywhere
/// 4. Clean mapping to HTTP responses: Success → 200, Failure → 400/404/etc.
///
/// USAGE EXAMPLES:
/// ===============
/// // In a command handler:
/// public async Task&lt;Result&lt;JobCard&gt;&gt; Handle(CreateJobCardCommand cmd)
/// {
///     var workOrder = await _repo.GetByIdAsync(cmd.WorkOrderId);
///     if (workOrder is null)
///         return Result.Failure&lt;JobCard&gt;(Error.NotFound("WorkOrder", cmd.WorkOrderId));
///
///     var jobCard = workOrder.CreateJobCard(cmd.Quantity);
///     return Result.Success(jobCard);
/// }
///
/// // In a controller:
/// var result = await _mediator.Send(command);
/// return result.IsSuccess
///     ? Ok(result.Value)
///     : BadRequest(result.Error);
///
/// DESIGN PATTERN: Railway Oriented Programming / Result Pattern
/// </summary>
public class Result
{
    protected Result(bool isSuccess, Error error)
    {
        if (isSuccess && error != Error.None)
            throw new InvalidOperationException("Success result cannot have an error.");
        if (!isSuccess && error == Error.None)
            throw new InvalidOperationException("Failure result must have an error.");

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public Error Error { get; }

    public static Result Success() => new(true, Error.None);
    public static Result Failure(Error error) => new(false, error);
    public static Result<TValue> Success<TValue>(TValue value) => new(value, true, Error.None);
    public static Result<TValue> Failure<TValue>(Error error) => new(default, false, error);
}

/// <summary>
/// Generic Result that carries a value on success.
/// Result&lt;JobCard&gt; means: either a JobCard or an Error.
/// </summary>
public class Result<TValue> : Result
{
    private readonly TValue? _value;

    internal Result(TValue? value, bool isSuccess, Error error)
        : base(isSuccess, error)
    {
        _value = value;
    }

    /// <summary>
    /// The success value. Throws if you access this on a failure result.
    /// Always check IsSuccess first!
    /// </summary>
    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Cannot access Value of a failure result. Check IsSuccess first.");

    /// <summary>Implicit conversion: allows returning a value directly as Result&lt;T&gt;</summary>
    public static implicit operator Result<TValue>(TValue? value) =>
        value is not null ? Success(value) : Failure<TValue>(Error.NullValue);
}
