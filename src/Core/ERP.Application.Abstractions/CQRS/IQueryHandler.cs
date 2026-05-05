using ERP.SharedKernel.Results;
using Mediator;

namespace ERP.Application.Abstractions.CQRS;

/// <summary>
/// QUERY HANDLER — Processes a query and returns data.
/// Each query has exactly ONE handler. MediatR routes the query to it automatically.
///
/// DESIGN PATTERN: Query Handler Pattern
/// This is the "Q" in CQRS — the read side.
/// </summary>
public interface IQueryHandler<in TQuery, TResponse> : IRequestHandler<TQuery, Result<TResponse>>
    where TQuery : IQuery<TResponse> { }
