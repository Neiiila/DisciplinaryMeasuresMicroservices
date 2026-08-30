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
        var links = await db.Employees
            .AsNoTracking()
            .Where(e => e.IsActive)
            .Select(e => new { e.Id, e.SupervisorId })
            .ToDictionaryAsync(e => e.Id, e => e.SupervisorId, StringComparer.OrdinalIgnoreCase, ct);

        var chain = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { employeeId };

        var current = links.GetValueOrDefault(employeeId);

        while (current is not null && chain.Count < MaxDepth)
        {
            // Stop on a repeat rather than looping forever.
            if (!seen.Add(current))
            {
                break;
            }

            chain.Add(current);
            current = links.GetValueOrDefault(current);
        }

        return chain;
    }
}
