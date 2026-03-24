using ERP.SharedKernel.Results;
using MediatR;

namespace ERP.Application.Abstractions.CQRS;

/// <summary>
/// COMMAND HANDLER — Processes a command and returns a result.
/// Each command has exactly ONE handler. MediatR routes the command to it automatically.
///
/// DESIGN PATTERN: Command Handler Pattern
/// This is the "C" in CQRS — the write side.
/// </summary>
public interface ICommandHandler<in TCommand> : IRequestHandler<TCommand, Result>
    where TCommand : ICommand { }

public interface ICommandHandler<in TCommand, TResponse> : IRequestHandler<TCommand, Result<TResponse>>
    where TCommand : ICommand<TResponse> { }
