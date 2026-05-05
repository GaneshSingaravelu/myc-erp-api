using System.Diagnostics;
using Mediator;
using Microsoft.Extensions.Logging;

namespace ERP.Application.Abstractions.Behaviors;

/// <summary>
/// LOGGING BEHAVIOR — Automatically logs every command/query with execution time.
///
/// This behavior runs in the MediatR pipeline and logs:
/// - When a request starts
/// - When a request completes
/// - How long it took (helps identify slow operations)
/// - If it took too long (warning threshold: 500ms)
///
/// DESIGN PATTERN: Cross-Cutting Concern via Pipeline
///
/// NOTE: We inject ILogger&lt;LoggingBehavior&gt; — this uses the logging abstraction,
/// not Serilog directly. The actual log output (console, file, Seq) is configured
/// in the ERP.Infrastructure project. This is Dependency Inversion in action.
/// </summary>
public sealed class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull, IMessage
{
    private readonly ILogger<LoggingBehavior<TRequest, TResponse>> _logger;

    public LoggingBehavior(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    {
        _logger = logger;
    }

    public async ValueTask<TResponse> Handle(
        TRequest request,
        MessageHandlerDelegate<TRequest, TResponse> next,
        CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;

        _logger.LogInformation("Handling {RequestName}: {@Request}", requestName, request);

        var stopwatch = Stopwatch.StartNew();
        var response = await next(request, cancellationToken);
        stopwatch.Stop();

        if (stopwatch.ElapsedMilliseconds > 500)
        {
            _logger.LogWarning("SLOW REQUEST: {RequestName} took {ElapsedMs}ms",
                requestName, stopwatch.ElapsedMilliseconds);
        }
        else
        {
            _logger.LogInformation("Handled {RequestName} in {ElapsedMs}ms",
                requestName, stopwatch.ElapsedMilliseconds);
        }

        return response;
    }
}
