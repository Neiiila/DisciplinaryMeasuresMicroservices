using Microsoft.EntityFrameworkCore;
using Sanctions.Api.Infrastructure;

namespace Sanctions.Api.Application;

public interface ISupervisorChainResolver
{
    Task<IReadOnlyList<string>> ResolveAsync(string employeeId, CancellationToken ct = default);
}

/// <summary>
/// Walks the supervisor chain above an employee, from the local directory projection.
/// </summary>
/// <remarks>
/// Reading from the projection rather than calling Identity keeps this service
/// available when Identity is not, and keeps the walk to a single query.
/// </remarks>
public sealed class SupervisorChainResolver(SanctionsDbContext db) : ISupervisorChainResolver
{
    /// <summary>
    /// Depth limit. A cycle should be impossible — Identity rejects
    /// self-supervision — but a projection assembled from events could still
    /// contain one if two updates interleaved badly, and an unbounded walk would
    /// hang the request rather than fail it.
    /// </summary>
    private const int MaxDepth = 10;

    public async Task<IReadOnlyList<string>> ResolveAsync(string employeeId, CancellationToken ct = default)
    {
        // One query, then walk in memory: the chain is short and the whole
        // directory is small enough that a recursive CTE would not pay for itself.
        // Inactive people are loaded too, because the walk has to pass through
        // them to reach the level above.
        var links = await db.Employees
            .AsNoTracking()
            .Select(e => new { e.Id, e.SupervisorId, e.IsActive })
            .ToDictionaryAsync(e => e.Id, e => (e.SupervisorId, e.IsActive), StringComparer.OrdinalIgnoreCase, ct);

        var chain = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { employeeId };

        var current = links.GetValueOrDefault(employeeId).SupervisorId;
        var steps = 0;

        while (current is not null && steps < MaxDepth)
        {
            steps++;

            // Stop on a repeat rather than looping forever.
            if (!seen.Add(current))
            {
                break;
            }

            var link = links.GetValueOrDefault(current);

            // Someone who has left the organisation is stepped over rather than
            // routed to: a request assigned to them could never be answered, and
            // would sit in the chain until it was cancelled. Their own supervisor
            // inherits the step.
            if (link.IsActive)
            {
                chain.Add(current);
            }

            current = link.SupervisorId;
        }

        return chain;
    }
}
