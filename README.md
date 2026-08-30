# Disciplinary Measures — Microservices

A microservices decomposition of the Disciplinary Measures platform: employee disciplinary procedures,
raised against an employee, routed up a supervisor approval chain, with live notifications.

Built on **.NET 10** with **MassTransit/RabbitMQ**, **YARP**, **SQL Server** and **OpenTelemetry**.

> This repository is a **parallel implementation**, not a replacement. The monolithic version remains
> the reference and lives on unchanged in the sibling `DisciplinaryMeasures` repository. Both expose the
> same HTTP contract, so the React client in `DisciplinaryMeasuresFrontend` runs against either.

---

## Table of contents

- [Why this exists](#why-this-exists)
- [Architecture](#architecture)
- [The services](#the-services)
- [The central problem: splitting the supervisor chain](#the-central-problem-splitting-the-supervisor-chain)
- [Messaging](#messaging)
- [Cross-cutting concerns](#cross-cutting-concerns)
- [The domain](#the-domain)
- [Running it](#running-it)
- [Configuration](#configuration)
- [Testing](#testing)
- [Continuous integration](#continuous-integration)
- [Project layout](#project-layout)
- [What the split cost](#what-the-split-cost)
- [Not yet done](#not-yet-done)

---

## Why this exists

The monolith is a well-factored clean-architecture application, and for its current load it is the
right shape. This repository explores what changes when the same domain is split along service
boundaries — and, more usefully, **what problems the split creates that the monolith did not have**.

Those problems are the interesting part of this codebase, and each is documented where it is solved:

| Problem the split creates | Where it is answered |
|---|---|
| Sanctions needs data Identity owns | [Directory projection](#the-central-problem-splitting-the-supervisor-chain) |
| A write and a publish are no longer one transaction | [Transactional outbox](#transactional-outbox) |
| Messages arrive twice, and out of order | [Idempotent consumers](#idempotent-consumers) |
| Three origins for one browser | [Gateway](#gateway) |
| Three services must agree on identity | [Shared token validation](#authentication) |

---

## Architecture

```
                         ┌───────────────────────┐
   browser ──────────────▶   Gateway (YARP)      │  :5100
                         │   CORS · JWT · routes │
                         └───────────┬───────────┘
                                     │ HTTP
              ┌──────────────────────┼──────────────────────┐
              ▼                      ▼                      ▼
    ┌──────────────────┐  ┌──────────────────┐  ┌──────────────────────┐
    │    Identity      │  │    Sanctions     │  │    Notifications     │
    │      :5101       │  │      :5102       │  │        :5103         │
    │                  │  │                  │  │   + SignalR hub      │
    │ users · accounts │  │ requests · faults│  │  feed · live push    │
    │ roles · tokens   │  │ directory copy   │  │                      │
    └────────┬─────────┘  └────────┬─────────┘  └──────────┬───────────┘
             │                     │                       │
        ┌────▼────┐           ┌────▼────┐             ┌────▼────┐
        │Identity │           │Sanctions│             │Notif.   │
        │   DB    │           │   DB    │             │   DB    │
        └─────────┘           └─────────┘             └─────────┘
             │                     │                       │
             └──────────── RabbitMQ (MassTransit) ─────────┘
                    integration events, at-least-once
```

Three rules hold this together:

1. **Database per service.** No service reads another's tables. This is the boundary that makes the
   split real — sharing a database would leave three deployables with one schema, which is worse than
   a monolith, not better.
2. **Events, not calls, between services.** No service calls another over HTTP on a request path. A
   service that is down degrades what depends on it, rather than taking it down too.
3. **The gateway is a filter, never the only check.** Every service authorises independently, because
   anything that reaches a service directly must still be rejected.

---

## The services

### Identity

Owns **employee records, sign-in accounts, roles**, and is the only service that issues tokens.

Employee and account are one aggregate deliberately: a record can exist without credentials (an
employee who never signs in), but credentials cannot exist without a record. Making that one entity
turns "has an account" into a property rather than a join every query has to remember.

Two rules the domain enforces that the monolith left to call sites:

- **Self-supervision is rejected.** A user supervising themselves produces a validation chain that
  never terminates.
- **Sign-in eligibility lives on the entity** (`EnsureCanSignIn`), so `Pending` and `Revoked` are
  answered identically everywhere rather than at whichever call site remembered to check.

Sign-in also verifies a dummy hash when no user matches, so a missing account and a wrong password take
the same time to answer — otherwise timing alone reveals which matriculation numbers exist. And
self-registration always yields a `Guest`: taking the role from the request body would let a caller
choose their own privileges.

### Sanctions

Owns the **request workflow**, the **fault catalogue**, and a **local projection of the employee
directory**.

The entire approval workflow lives on the `SanctionRequest` aggregate. In the monolith it was spread
across four service methods that each re-read and re-wrote the row, which produced two defects: the
next validator was read from a navigation property that was never loaded, so the chain silently
stalled; and a duplicate decision advanced the progress counter before the duplicate check ran. Keeping
the transition in one method means the guard and the state change cannot be reordered or skipped.

### Notifications

A **pure consumer**. Nothing calls it to create a notification — it reacts to what the other services
announce, and holds a feed plus a SignalR hub for live delivery.

That inversion is the reason to extract it: the sanction workflow no longer needs to know anyone is
listening, so adding email or push later is a new consumer here rather than a change to the request
code.

### Gateway

**YARP** in front of the three, so the browser has one origin and one certificate to trust, and the
internal topology can change without the client noticing. CORS lives here alone — the browser only ever
talks to the gateway, so a policy on each service would be three places to keep in step for no benefit.

Active health probes against `/health/ready` stop the proxy routing to an instance whose database is
still coming up.

---

## The central problem: splitting the supervisor chain

This is the decision the whole design turns on.

**The problem.** Raising a sanction request needs the employee's supervisor chain — who validates, and
in what order. That data belongs to Identity. Sanctions needs it on every raise and every decision.

**The obvious answer, and why it is wrong.** Sanctions could call `GET /api/users/{id}` on Identity.
That is simple, always fresh, and couples the two services' availability: Identity down means no
request can be raised *or decided* anywhere in the platform. It also puts a network round trip — several,
for a chain — on every request. Splitting the monolith to gain a distributed single point of failure is
worse than not splitting it.

**What this codebase does instead.** Identity publishes `EmployeeProfileChanged` whenever a record
changes. Sanctions consumes it into its own `EmployeeProjection` table and reads the chain locally:

```csharp
// SupervisorChainResolver — one query, then walk in memory.
var links = await db.Employees
    .AsNoTracking()
    .Select(e => new { e.Id, e.SupervisorId, e.IsActive })
    .ToDictionaryAsync(/* ... */);
```

**The trade, stated plainly.** The copy is eventually consistent. A supervisor reassigned seconds ago
may route one request to the previous manager.

That is acceptable *here* for a specific reason: **the chain is re-resolved at every decision**, not
stored on the request when it is raised. A request corrects itself as it travels up, so a stale read
costs at most one misrouted step rather than a permanently wrong chain. If the workflow had frozen the
chain at raise time, this design would not be defensible.

Three details make the projection safe to rely on:

- **Events carry full snapshots, not deltas.** A consumer that misses one converges on the next, which
  makes the projection self-healing rather than permanently skewed.
- **Older events are ignored.** `EmployeeProjection.Apply` compares timestamps and returns early, so a
  redelivered older snapshot cannot roll newer state backwards.
- **Departed supervisors are stepped over, not routed to.** A request assigned to someone who has left
  could never be answered and would sit in the chain until cancelled; their own manager inherits the
  step. *(This one was found by a test, not by design — see [Testing](#testing).)*

---

## Messaging

### Transactional outbox

Writing an entity and publishing an event are two different systems. Without care, a broker outage
between `SaveChanges` and `Publish` leaves a user created that no other service ever hears about — the
classic dual-write failure, and one that is invisible until someone notices the projection has drifted.

MassTransit's EF Core outbox writes the message to the service's **own database inside the same
transaction** as the entity change, then dispatches it in the background:

```csharp
bus.AddEntityFrameworkOutbox<IdentityDbContext>(outbox =>
{
    outbox.UseSqlServer();
    outbox.UseBusOutbox();
});
```

Either both the row and the message are committed, or neither is.

### Idempotent consumers

RabbitMQ guarantees **at-least-once** delivery, not exactly-once. Every consumer must therefore tolerate
seeing the same message twice, and the two here do it differently because their work differs:

- **Directory projection** — idempotent by construction. The event is a full snapshot, so applying it
  twice produces the same row.
- **Notifications** — not naturally idempotent, since inserting twice means notifying twice. Each
  notification records the `SourceEventId` that produced it, and a **unique index on
  `(SourceEventId, UserId)`** enforces the rest. The query-then-insert check alone would let two
  concurrent deliveries through; the index is what actually prevents the duplicate, and the resulting
  insert conflict is caught and treated as success.

### Retry

Transient faults are retried in place with backoff (`200ms → 1s → 5s`), after which the message moves
to an error queue rather than blocking everything behind it.

### Events

| Event | Published by | Consumed by |
|---|---|---|
| `EmployeeProfileChanged` | Identity | Sanctions → directory projection |
| `EmployeeRemoved` | Identity | Sanctions → deactivate projection row |
| `AccountAwaitingActivation` | Identity | Notifications → alert administrators |
| `SanctionRequestRaised` | Sanctions | *(available; no consumer yet)* |
| `SanctionRequestAwaitingValidator` | Sanctions | Notifications → tell the validator |
| `SanctionRequestSettled` | Sanctions | Notifications → tell the requester |

Integration events are a **published contract**: once another service consumes one, its shape may be
extended but never changed. They carry primitives and ids, never domain objects, so no service takes a
compile-time dependency on another's model.

---

## Cross-cutting concerns

Anything not centralised in `BuildingBlocks` has to be remembered four times, so these live in one
place and every service applies them identically.

### Authentication

Identity signs; everyone validates with the **same parameters**, configured once in
`JwtAuthenticationExtensions`. A service configuring its own validation is how an audience or issuer
check quietly drifts and stops being enforced. `ClockSkew` is set to zero — the five-minute default
silently extends every token's life.

### Error handling

One mapping from `ErrorType` to HTTP status, shared by all four services, so "conflict" means 409
everywhere. Failures are returned as RFC 7807 problem details with a stable `code` extension that
clients branch on; the message is display text and may be reworded freely.

Expected failures are **returned, not thrown** — a duplicate email is not an exceptional condition —
which leaves the exception handler free to answer 500 for everything it genuinely sees.

### Health

`/health/live` answers "is the process up"; `/health/ready` additionally requires dependencies tagged
`ready`, so an orchestrator does not route traffic to an instance whose database is still starting.

### Observability

OpenTelemetry tracing and metrics on every service. Trace context propagates across HTTP *and* through
MassTransit, so a request that raises a sanction and ends in a pushed notification is one trace across
three processes — which is the only practical way to debug an event-driven flow.

### Serialisation

Enums travel as **names**, configured once. If one service emitted ordinals while the others emitted
names, the client would see the same field as a number on some responses and a string on others.

---

## The domain

A **sanction request** is raised by one user against an employee, citing a **fault** — either one from
the catalogue or a new one proposed inline, which stays unvalidated until an administrator accepts it.

The request travels up the employee's **supervisor chain**. At any moment it awaits exactly one
validator. Each records a decision — `Approved`, `Refused` or `Missed` — and progress advances (`2/3`).
A refusal closes the request immediately; collecting the whole chain closes it too. The requester may
cancel their own request while it is open.

**Roles.** `Guest` is read-only and is what registration produces. `Employee` may raise requests and
answer those addressed to them. `Administrator` additionally manages users, activates accounts, and
sees every request.

**Account lifecycle.** Registration creates a `Pending` account and issues **no token**; an
administrator must activate it. Access can later be `Revoked` without deleting the employee record.

---

## Running it

### With Docker (everything)

```bash
export JWT_KEY="$(openssl rand -base64 48)"
```

```bash
docker compose up --build
```

| Service | URL |
|---|---|
| Gateway | http://localhost:5100 |
| Identity | http://localhost:5101/scalar/v1 |
| Sanctions | http://localhost:5102/scalar/v1 |
| Notifications | http://localhost:5103/scalar/v1 |
| RabbitMQ management | http://localhost:15672 (guest/guest) |

`JWT_KEY` has no default and compose refuses to start without it. Every service needs the same value —
Identity signs with it, the others validate against it — and a default would be a signing key committed
to the repository.

### Locally, without Docker

Infrastructure only, then the services from your IDE or four terminals:

```bash
docker compose up sql rabbitmq
```

```bash
dotnet run --project src/Services/Identity/Identity.Api
```

Set the signing key once per service via user-secrets:

```bash
dotnet user-secrets set "Jwt:Key" "<32+ character key>" --project src/Services/Identity/Identity.Api
```

### With the React client

Point `DisciplinaryMeasuresFrontend` at the **gateway**, not at a service:

```
VITE_API_BASE_URL=http://localhost:5100
```

The gateway's CORS policy already allows `http://localhost:5173`.

---

## Configuration

Every setting is overridable through the environment using ASP.NET's `__` separator
(`ConnectionStrings__IdentityDb`).

| Setting | Applies to | Notes |
|---|---|---|
| `Jwt:Key` | all | **Required.** Must be identical across services |
| `Jwt:Issuer` / `Jwt:Audience` | all | Must match what Identity signs |
| `ConnectionStrings:IdentityDb` | Identity | |
| `ConnectionStrings:SanctionsDb` | Sanctions | |
| `ConnectionStrings:NotificationsDb` | Notifications | |
| `ConnectionStrings:MessageBroker` | all but gateway | RabbitMQ AMQP URI |
| `Cors:AllowedOrigins` | gateway | Array; the browser only talks to the gateway |
| `ReverseProxy:Clusters:*` | gateway | Destination addresses |

---

## Testing

```bash
dotnet test
```

**44 tests**, concentrated on the two places this architecture can go wrong: the workflow invariants on
the aggregates, and the eventual-consistency behaviour of the projection that replaced a synchronous
call.

| Area | What it pins down |
|---|---|
| `SanctionRequestTests` (16) | Chain advancement; refusal closing immediately; only the awaited validator may answer; a duplicate decision changing nothing; a settled request accepting nothing further; cancellation limited to the requester |
| `SupervisorChainResolverTests` (5) | The walk; an employee with nobody above them; a cycle terminating; departed supervisors being stepped over |
| `DirectoryProjectionTests` (4) | Redelivery leaving the row unchanged; a newer event replacing state; an **out-of-order older event being ignored** |
| `UserTests` (12) | Accounts opening `Pending`; activation and revocation; self-supervision rejected; soft delete reporting generic credentials |
| `Pbkdf2PasswordHasherTests` (7) | Round-trip; per-password salt; malformed stored hashes failing closed |

The projection and hasher tests are the ones worth having: they cover behaviour that is invisible in
normal operation and only shows up under retry, concurrency, or attack.

### A bug these tests found

Writing `A_departed_supervisor_is_skipped_and_their_own_manager_inherits_the_step` surfaced a real
defect. The chain walk filtered inactive employees out of the lookup entirely, so a departed
supervisor was still assigned the step *and* the level above them became unreachable. A request routed
to someone who has left can never be answered and sits in the chain until cancelled.

The walk now passes **through** an inactive link without adding it, so their own manager inherits the
step. That is the kind of failure that would have been near-impossible to diagnose in production —
requests quietly stuck, with nothing in the logs.

---

## Continuous integration

`.github/workflows/ci.yml`:

```
build-and-test ──► images (identity · sanctions · notifications · gateway, in parallel)
compose (independent)
```

- **build-and-test** — restore, build, test with coverage. Warnings are errors solution-wide, so this
  gate also catches vulnerable-package advisories.
- **images** — all four built in parallel with `fail-fast: false`, so one broken Dockerfile does not
  hide the state of the other three. Layer caching via GitHub Actions cache, scoped per service.
- **compose** — validates the stack parses and interpolates.

Dependabot opens weekly NuGet updates grouped by area, so a framework bump arrives as one reviewable
pull request rather than a dozen that must be merged in the right order.

---

## Project layout

```
src/
  BuildingBlocks/
    BuildingBlocks.Core/         Result, Error, IClock — no dependencies
    BuildingBlocks.Messaging/    IntegrationEvent + the published event contracts
    BuildingBlocks.Web/          JWT setup, ProblemDetails mapping, health, telemetry
  Services/
    Identity/Identity.Api/       Domain · Application · Infrastructure · Endpoints
    Sanctions/Sanctions.Api/     + Consumers
    Notifications/Notifications.Api/  + Consumers · Realtime
  Gateway/Gateway.Api/           YARP configuration
tests/
  Identity.Api.Tests/
  Sanctions.Api.Tests/
```

**Why one project per service, not four.** The monolith separates Domain, Application, Infrastructure
and Api into assemblies, which is right when they are large and shared. Here each service is small and
owns its whole stack, so the same separation is expressed as folders. Assembly boundaries would add
twelve projects and enforce a layering that the service boundary already enforces more strongly — the
thing they would have prevented, a service reaching into another's internals, is now impossible by
construction because they are separate processes with separate databases.

---

## What the split cost

An honest accounting, because these are real and the monolith pays none of them:

- **Eventual consistency.** A supervisor change takes effect in Sanctions when the event is consumed,
  not when it is saved.
- **More moving parts.** A broker and three databases where there was one database. Local development
  needs Docker.
- **Distributed debugging.** A single logical operation spans processes. Tracing makes this tractable,
  not free.
- **Duplicated shape.** `EmploymentDetails` exists in Identity; a subset of it is projected into
  Sanctions. That is the price of not sharing a database, and the copy must be kept deliberately narrow.
- **Slower changes that cross a boundary.** Adding a field the projection needs means an event change,
  a consumer change and a deployment order. In the monolith it is one migration.

For the current load, **the monolith is the better choice**. This repository is worth having for what
it demonstrates and for the point at which the load, the team size, or an independent scaling
requirement changes that answer.

---

## Not yet done

- **Migrations.** The DbContexts are complete but no EF migrations are generated; the databases are not
  created on startup. `dotnet ef migrations add InitialSchema` per service is the next step.
- **File storage.** Photo and attachment upload exist in the monolith and are not yet ported; the
  contracts carry the paths but nothing writes them.
- **Fault validation endpoint.** Proposed faults are created unvalidated with no administrator route to
  accept them.
- **Saga.** The workflow is currently a chain of independent events. A long-running one — escalation
  after a validator misses a deadline — would want MassTransit's state machine rather than more
  consumers.
- **Integration tests.** The suite is unit-level. `WebApplicationFactory` plus Testcontainers for SQL
  Server and RabbitMQ would cover the wiring these tests deliberately do not.
