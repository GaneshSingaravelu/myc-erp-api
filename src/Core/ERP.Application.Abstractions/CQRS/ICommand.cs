using ERP.SharedKernel.Results;
using MediatR;

namespace ERP.Application.Abstractions.CQRS;

/// <summary>
/// COMMAND — Represents an action that CHANGES data (Create, Update, Delete).
///
/// WHAT IS CQRS? (Very Common Interview Question!)
/// =================================================
/// CQRS = Command Query Responsibility Segregation
///
/// The idea: SEPARATE read operations from write operations.
///
/// COMMAND (Write):                    QUERY (Read):
/// - Creates/Updates/Deletes data     - Only reads data, never modifies
/// - Returns Result (success/failure) - Returns data (DTO)
/// - Goes through validation          - Can be optimized (skip validation)
/// - Raises domain events             - Can use caching
/// - Example: CreateJobCardCommand     - Example: GetJobCardByIdQuery
///
/// WHY CQRS?
/// ==========
/// 1. Single Responsibility: Each handler does ONE thing
/// 2. Scalability: Read and write can be optimized independently
/// 3. Testing: Each handler is small and easy to test
/// 4. Clarity: Looking at a command, you know it changes something
///
/// HOW IT WORKS WITH MediatR:
/// ===========================
/// Controller → sends ICommand via MediatR → MediatR finds the right Handler → Handler executes
///
/// EXAMPLE:
/// ========
/// // Define the command:
/// public record CreateJobCardCommand(Guid WorkOrderId, int Quantity) : ICommand&lt;Guid&gt;;
///
/// // Define the handler:
/// public class CreateJobCardHandler : ICommandHandler&lt;CreateJobCardCommand, Guid&gt;
/// {
///     public async Task&lt;Result&lt;Guid&gt;&gt; Handle(CreateJobCardCommand cmd, CancellationToken ct)
///     {
///         var jobCard = new JobCard(cmd.WorkOrderId, cmd.Quantity);
///         await _repo.AddAsync(jobCard, ct);
///         return jobCard.Id;  // implicit conversion to Result&lt;Guid&gt;
///     }
/// }
///
/// // In the controller:
/// [HttpPost]
/// public async Task&lt;IActionResult&gt; Create(CreateJobCardRequest request)
/// {
///     var command = new CreateJobCardCommand(request.WorkOrderId, request.Quantity);
///     var result = await _mediator.Send(command);
///     return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
/// }
///
/// DESIGN PATTERN: Command Pattern + Mediator Pattern
/// </summary>
public interface ICommand : IRequest<Result> { }

/// <summary>
/// Command that returns a value on success.
/// ICommand&lt;Guid&gt; means: "This command returns a Guid on success, or an Error on failure."
/// </summary>
public interface ICommand<TResponse> : IRequest<Result<TResponse>> { }
