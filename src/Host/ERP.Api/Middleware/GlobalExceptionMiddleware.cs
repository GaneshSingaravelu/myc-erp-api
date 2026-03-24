using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ERP.Api.Middleware;

/// <summary>
/// GLOBAL EXCEPTION MIDDLEWARE — Catches ALL unhandled exceptions in the API.
///
/// WHY GLOBAL EXCEPTION HANDLING?
/// ================================
/// Without this, unhandled exceptions would:
/// - Return ugly HTML error pages (not useful for Angular)
/// - Potentially leak sensitive information (stack traces, connection strings)
/// - Not be logged properly
///
/// With this middleware:
/// - Every unhandled exception returns a clean JSON error response
/// - Every exception is logged with full details (for debugging)
/// - Sensitive details are hidden from the client
/// - Angular always receives a consistent error format
///
/// RESPONSE FORMAT:
///   {
///     "statusCode": 500,
///     "message": "An internal server error occurred.",
///     "traceId": "abc-123"  ← For support team to find the log entry
///   }
///
/// DESIGN PATTERN: Exception Handling Middleware
/// </summary>
public class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            // Try to process the request normally
            await _next(context);
        }
        catch (Exception ex)
        {
            // Something went wrong — log it and return a clean error response
            var traceId = context.TraceIdentifier;
            _logger.LogError(ex, "Unhandled exception. TraceId: {TraceId}", traceId);

            context.Response.ContentType = "application/json";
            context.Response.StatusCode = (int)GetStatusCode(ex);

            var response = new
            {
                statusCode = context.Response.StatusCode,
                message = GetClientMessage(ex),
                traceId
            };

            await context.Response.WriteAsync(
                JsonSerializer.Serialize(response, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }));
        }
    }

    /// <summary>Map exception types to HTTP status codes</summary>
    private static HttpStatusCode GetStatusCode(Exception ex) => ex switch
    {
        ArgumentException => HttpStatusCode.BadRequest,                // 400
        UnauthorizedAccessException => HttpStatusCode.Unauthorized,    // 401
        KeyNotFoundException => HttpStatusCode.NotFound,               // 404
        InvalidOperationException => HttpStatusCode.Conflict,          // 409
        _ => HttpStatusCode.InternalServerError                        // 500
    };

    /// <summary>Return safe messages to the client (no stack traces!)</summary>
    private static string GetClientMessage(Exception ex) => ex switch
    {
        ArgumentException => ex.Message,
        UnauthorizedAccessException => "You are not authorized to perform this action.",
        KeyNotFoundException => ex.Message,
        InvalidOperationException => ex.Message,
        _ => "An internal server error occurred. Please contact support."
    };
}
