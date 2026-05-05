using ERP.SharedKernel.Results;
using FluentValidation;
using Mediator;

namespace ERP.Application.Abstractions.Behaviors;

/// <summary>
/// VALIDATION BEHAVIOR — Automatically validates EVERY command/query before it reaches the handler.
///
/// WHAT IS A PIPELINE BEHAVIOR? (Interview Question!)
/// ====================================================
/// A Pipeline Behavior is like middleware for MediatR.
/// It intercepts every request BEFORE it reaches the handler.
///
/// REQUEST FLOW:
/// Controller → MediatR.Send(command)
///   → [ValidationBehavior] ← validates the command
///     → [LoggingBehavior]  ← logs the request
///       → [Handler]        ← actual business logic
///
/// If validation fails, the request NEVER reaches the handler.
/// The behavior returns a failure Result with validation errors.
///
/// DESIGN PATTERN: Chain of Responsibility / Pipeline Pattern
///
/// INTERVIEW TIP: "We use MediatR pipeline behaviors for cross-cutting concerns
/// like validation and logging. This keeps handlers focused on business logic only."
///
/// HOW FluentValidation WORKS:
/// ============================
/// 1. You define a Validator class for each command:
///    public class CreateJobCardValidator : AbstractValidator&lt;CreateJobCardCommand&gt;
///    {
///        public CreateJobCardValidator()
///        {
///            RuleFor(x => x.Quantity).GreaterThan(0).WithMessage("Quantity must be positive");
///            RuleFor(x => x.WorkOrderId).NotEmpty().WithMessage("Work Order is required");
///        }
///    }
///
/// 2. This behavior AUTOMATICALLY discovers and runs the validator.
///    You don't need to call it manually — it's registered in DI.
/// </summary>
public sealed class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull, IMessage
    where TResponse : Result
{
    private readonly IEnumerable<IValidator<TRequest>> _validators;

    public ValidationBehavior(IEnumerable<IValidator<TRequest>> validators)
    {
        _validators = validators;
    }

    public async ValueTask<TResponse> Handle(
        TRequest request,
        MessageHandlerDelegate<TRequest, TResponse> next,
        CancellationToken cancellationToken)
    {
        if (!_validators.Any())
            return await next(request, cancellationToken);

        var context = new ValidationContext<TRequest>(request);
        var validationResults = await Task.WhenAll(
            _validators.Select(v => v.ValidateAsync(context, cancellationToken)));

        var errors = validationResults
            .SelectMany(r => r.Errors)
            .Where(e => e is not null)
            .ToList();

        if (errors.Count != 0)
        {
            var errorMessages = string.Join("; ", errors.Select(e => e.ErrorMessage));
            var error = Error.Validation("Validation.Failed", errorMessages);

            if (typeof(TResponse) == typeof(Result))
                return (TResponse)(object)Result.Failure(error);

            var resultType = typeof(TResponse).GetGenericArguments()[0];
            var failureMethod = typeof(Result)
                .GetMethod(nameof(Result.Failure), 1, [typeof(Error)])!
                .MakeGenericMethod(resultType);

            return (TResponse)failureMethod.Invoke(null, [error])!;
        }

        return await next(request, cancellationToken);
    }
}
