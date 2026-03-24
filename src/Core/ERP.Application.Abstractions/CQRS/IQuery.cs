using ERP.SharedKernel.Results;
using MediatR;

namespace ERP.Application.Abstractions.CQRS;

/// <summary>
/// QUERY — Represents a request that READS data without modifying anything.
///
/// QUERY vs COMMAND:
/// ==================
/// Command: "Create a job card" → changes database → returns Result
/// Query:   "Get job card #123" → reads database   → returns data
///
/// IMPORTANT RULE: Queries must NEVER modify data!
/// This is called "Command-Query Separation" (CQS).
///
/// EXAMPLE:
/// ========
/// // Define the query:
/// public record GetJobCardByIdQuery(Guid Id) : IQuery&lt;JobCardDto&gt;;
///
/// // Define the handler:
/// public class GetJobCardByIdHandler : IQueryHandler&lt;GetJobCardByIdQuery, JobCardDto&gt;
/// {
///     public async Task&lt;Result&lt;JobCardDto&gt;&gt; Handle(GetJobCardByIdQuery query, CancellationToken ct)
///     {
///         var jobCard = await _repo.GetByIdAsync(query.Id, ct);
///         if (jobCard is null)
///             return Result.Failure&lt;JobCardDto&gt;(Error.NotFound("JobCard", query.Id));
///         return new JobCardDto(jobCard.Id, jobCard.Number, ...);
///     }
/// }
/// </summary>
public interface IQuery<TResponse> : IRequest<Result<TResponse>> { }
