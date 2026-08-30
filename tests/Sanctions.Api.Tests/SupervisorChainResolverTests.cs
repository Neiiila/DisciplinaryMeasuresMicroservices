using BuildingBlocks.Messaging.Contracts;
using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sanctions.Api.Application;
using Sanctions.Api.Consumers;
using Sanctions.Api.Infrastructure;

namespace Sanctions.Api.Tests;

/// <summary>
/// The chain walk over the local directory projection, and the consumer that
/// keeps that projection current. Together these are the part of the split that
/// replaces a synchronous call into Identity.
/// </summary>
public sealed class SupervisorChainResolverTests : IDisposable
{
    private readonly SanctionsDbContext _db = new(
        new DbContextOptionsBuilder<SanctionsDbContext>()
            .UseInMemoryDatabase($"chain-{Guid.NewGuid()}")
            .Options);

    private async Task SeedAsync(params (string Id, string? SupervisorId)[] employees)
    {
        var consumer = new EmployeeProfileChangedConsumer(_db, NullLogger<EmployeeProfileChangedConsumer>.Instance);

        foreach (var (id, supervisorId) in employees)
        {
            await consumer.Consume(ContextFor(new EmployeeProfileChanged
            {
                EmployeeId = id,
                FullName = $"Person {id}",
                SupervisorId = supervisorId,
                Role = "Employee",
                IsActive = true
            }));
        }
    }

    private static ConsumeContext<T> ContextFor<T>(T message) where T : class
    {
        var context = Substitute.For<ConsumeContext<T>>();
        context.Message.Returns(message);
        context.CancellationToken.Returns(CancellationToken.None);
        return context;
    }

    [Fact]
    public async Task It_walks_every_level_above_the_employee()
    {
        await SeedAsync(("EMP1", "SUP1"), ("SUP1", "SUP2"), ("SUP2", null));

        var chain = await new SupervisorChainResolver(_db).ResolveAsync("EMP1");

        chain.Should().Equal("SUP1", "SUP2");
    }

    [Fact]
    public async Task An_employee_with_no_supervisor_has_an_empty_chain()
    {
        await SeedAsync(("EMP1", null));

        var chain = await new SupervisorChainResolver(_db).ResolveAsync("EMP1");

        chain.Should().BeEmpty();
    }

    /// <summary>
    /// Identity rejects self-supervision, but the projection is assembled from
    /// events and could still contain a cycle if two updates interleaved badly.
    /// An unbounded walk would hang the request rather than fail it.
    /// </summary>
    [Fact]
    public async Task A_cycle_terminates_instead_of_looping_forever()
    {
        await SeedAsync(("EMP1", "SUP1"), ("SUP1", "SUP2"), ("SUP2", "SUP1"));

        var chain = await new SupervisorChainResolver(_db).ResolveAsync("EMP1");

        chain.Should().Equal("SUP1", "SUP2");
    }

    /// <summary>
    /// Someone who has left is stepped over, not routed to: a request assigned to
    /// them could never be answered and would sit in the chain until cancelled.
    /// </summary>
    [Fact]
    public async Task A_departed_supervisor_is_skipped_and_their_own_manager_inherits_the_step()
    {
        await SeedAsync(("EMP1", "SUP1"), ("SUP1", "SUP2"), ("SUP2", null));

        await new EmployeeRemovedConsumer(_db).Consume(
            ContextFor(new EmployeeRemoved { EmployeeId = "SUP1" }));

        var chain = await new SupervisorChainResolver(_db).ResolveAsync("EMP1");

        chain.Should().Equal("SUP2");
    }

    [Fact]
    public async Task A_chain_of_only_departed_supervisors_is_empty()
    {
        await SeedAsync(("EMP1", "SUP1"), ("SUP1", null));

        await new EmployeeRemovedConsumer(_db).Consume(
            ContextFor(new EmployeeRemoved { EmployeeId = "SUP1" }));

        var chain = await new SupervisorChainResolver(_db).ResolveAsync("EMP1");

        chain.Should().BeEmpty();
    }

    public void Dispose() => _db.Dispose();
}

public sealed class DirectoryProjectionTests : IDisposable
{
    private readonly SanctionsDbContext _db = new(
        new DbContextOptionsBuilder<SanctionsDbContext>()
            .UseInMemoryDatabase($"projection-{Guid.NewGuid()}")
            .Options);

    private EmployeeProfileChangedConsumer Consumer =>
        new(_db, NullLogger<EmployeeProfileChangedConsumer>.Instance);

    private static ConsumeContext<T> ContextFor<T>(T message) where T : class
    {
        var context = Substitute.For<ConsumeContext<T>>();
        context.Message.Returns(message);
        context.CancellationToken.Returns(CancellationToken.None);
        return context;
    }

    private static EmployeeProfileChanged Event(
        string id,
        string name,
        string? supervisorId,
        DateTimeOffset occurredOn) => new()
        {
            EmployeeId = id,
            FullName = name,
            SupervisorId = supervisorId,
            Role = "Employee",
            IsActive = true,
            OccurredOn = occurredOn
        };

    private static readonly DateTimeOffset Earlier = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_first_event_creates_the_row()
    {
        await Consumer.Consume(ContextFor(Event("EMP1", "Amina Haddad", null, Earlier)));

        var employee = await _db.Employees.FindAsync("EMP1");
        employee!.FullName.Should().Be("Amina Haddad");
    }

    /// <summary>
    /// The broker guarantees at-least-once delivery, so the same event can arrive
    /// twice. A full snapshot applied twice must leave the same row.
    /// </summary>
    [Fact]
    public async Task Redelivering_the_same_event_changes_nothing()
    {
        var @event = Event("EMP1", "Amina Haddad", "SUP1", Earlier);

        await Consumer.Consume(ContextFor(@event));
        await Consumer.Consume(ContextFor(@event));

        _db.Employees.Count().Should().Be(1);
        (await _db.Employees.FindAsync("EMP1"))!.SupervisorId.Should().Be("SUP1");
    }

    [Fact]
    public async Task A_newer_event_replaces_the_stored_state()
    {
        await Consumer.Consume(ContextFor(Event("EMP1", "Amina Haddad", "SUP1", Earlier)));
        await Consumer.Consume(ContextFor(Event("EMP1", "Amina Alaoui", "SUP2", Later)));

        var employee = await _db.Employees.FindAsync("EMP1");
        employee!.FullName.Should().Be("Amina Alaoui");
        employee.SupervisorId.Should().Be("SUP2");
    }

    /// <summary>
    /// Retries can deliver an older snapshot after a newer one. Applying it would
    /// silently roll the projection backwards.
    /// </summary>
    [Fact]
    public async Task An_out_of_order_older_event_is_ignored()
    {
        await Consumer.Consume(ContextFor(Event("EMP1", "Amina Alaoui", "SUP2", Later)));
        await Consumer.Consume(ContextFor(Event("EMP1", "Amina Haddad", "SUP1", Earlier)));

        var employee = await _db.Employees.FindAsync("EMP1");
        employee!.FullName.Should().Be("Amina Alaoui");
        employee.SupervisorId.Should().Be("SUP2");
    }

    public void Dispose() => _db.Dispose();
}
