using BuildingBlocks.Messaging.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Sanctions.Api.Domain;
using Sanctions.Api.Infrastructure;

namespace Sanctions.Api.Consumers;

/// <summary>
/// Keeps the local employee projection current from Identity's events.
/// </summary>
/// <remarks>
/// Consumption is idempotent by construction: the event carries a full snapshot,
/// so applying it twice produces the same row, and re-applying an older snapshot
/// is ignored by the entity's own timestamp check. That matters because the
/// broker guarantees at-least-once delivery, not exactly-once.
/// </remarks>
public sealed class EmployeeProfileChangedConsumer(
    SanctionsDbContext db,
    ILogger<EmployeeProfileChangedConsumer> logger) : IConsumer<EmployeeProfileChanged>
{
    public async Task Consume(ConsumeContext<EmployeeProfileChanged> context)
    {
        var message = context.Message;

        var employee = await db.Employees
            .FirstOrDefaultAsync(e => e.Id == message.EmployeeId, context.CancellationToken);

        if (employee is null)
        {
            employee = EmployeeProjection.Create(message.EmployeeId, message.FullName, message.OccurredOn);
            db.Employees.Add(employee);
        }

        employee.Apply(
            message.FullName,
            message.Email,
            message.SupervisorId,
            message.Department,
            message.Position,
            message.IsActive,
            message.OccurredOn);

        await db.SaveChangesAsync(context.CancellationToken);

        logger.LogDebug("Directory projection updated for {EmployeeId}", message.EmployeeId);
    }
}

public sealed class EmployeeRemovedConsumer(SanctionsDbContext db) : IConsumer<EmployeeRemoved>
{
    public async Task Consume(ConsumeContext<EmployeeRemoved> context)
    {
        var employee = await db.Employees
            .FirstOrDefaultAsync(e => e.Id == context.Message.EmployeeId, context.CancellationToken);

        // The row is deactivated, never deleted: existing requests reference this
        // id and their history has to stay readable.
        employee?.Deactivate(context.Message.OccurredOn);

        await db.SaveChangesAsync(context.CancellationToken);
    }
}
