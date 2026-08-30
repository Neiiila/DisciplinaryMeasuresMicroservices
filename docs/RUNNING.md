# Running the platform

How to get the microservices backend and the React frontend running together, and what the moving
parts actually are.

---

## What you are starting

| Piece | Where it runs | Port |
|---|---|---|
| Gateway (YARP) | container | 5100 |
| Identity | container | 5101 |
| Sanctions | container | 5102 |
| Notifications | container | 5103 |
| SQL Server | container | 1433 |
| RabbitMQ | container | 5672 / 15672 |
| React frontend | your machine (`npm run dev`) | 5173 |

The frontend is **not** containerised. It runs from its own repository with Vite's dev server, which is
what gives you hot reload while working on it.

---

## About the databases

There are **three logical databases**, and they all live inside **one SQL Server container**:

```
┌─────────────────────────── sql container ───────────────────────────┐
│                                                                     │
│   DisciplinaryMeasures.Identity        ← only Identity connects     │
│   DisciplinaryMeasures.Sanctions       ← only Sanctions connects    │
│   DisciplinaryMeasures.Notifications   ← only Notifications connect │
│                                                                     │
└─────────────────────────────────────────────────────────────────────┘
```

**Why one container, three databases.** Running three SQL Server containers locally would cost about
1.5 GB of RAM for no benefit. The boundary that matters architecturally is that **no service can read
another's tables** — and that holds here, because each service is configured with a connection string
scoped to its own database and has no credentials or code path to reach the others.

In a real deployment these would typically be three separate instances (or three managed databases), and
nothing in the application code would change: only the connection strings differ.

**Data lives in a named Docker volume** (`sql-data`), so it survives `docker compose down` and restarts.
To wipe it and start clean, see [Resetting](#resetting-everything).

**Schema creation.** Each service applies its own EF migrations at startup — but **only in Development**.
That is deliberate: applying migrations from application startup in production means every replica races
to migrate the same database. Real environments migrate as a separate step before the new version starts.

---

## Prerequisites

- **Docker Desktop**, running
- **.NET 10 SDK** — only if you want to run services outside containers
- **Node 20+** — for the frontend

---

## Path A — everything in Docker (recommended)

### 1. Set the signing key

Every service needs the **same** key: Identity signs tokens with it, the others validate against it.
There is no default, and compose refuses to start without one — a default would be a signing key
committed to the repository.

```bash
export JWT_KEY="$(openssl rand -base64 48)"
```

On PowerShell:

```powershell
$env:JWT_KEY = [Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Max 256 }))
```

### 2. Start the stack

```bash
docker compose up --build
```

First run takes several minutes: it pulls the .NET SDK and runtime images and SQL Server. Later runs are
fast because the layers are cached.

### 3. Wait for readiness

The services retry their migrations while SQL Server finishes starting — SQL Server accepts TCP
connections before it will accept logins, so the first attempt often fails and retries. That is normal.

```bash
curl http://localhost:5101/health/ready
```

All four should answer `Healthy`:

```bash
for p in 5100 5101 5102 5103; do curl -s -o /dev/null -w "$p %{http_code}\n" http://localhost:$p/health/ready; done
```

### 4. Start the frontend

In the `DisciplinaryMeasuresFrontend` repository:

```bash
cp .env.example .env
```

Point it at the **gateway**, not at an individual service:

```
VITE_API_BASE_URL=http://localhost:5100
```

```bash
npm install && npm run dev
```

Open http://localhost:5173. The gateway already allows that origin.

---

## Path C — no Docker at all (Windows, LocalDB)

Useful when Docker is unavailable. The three databases run on **SQL Server LocalDB**, which ships with
Visual Studio and the SQL Server tooling, so nothing is containerised.

The one thing this path cannot provide is **RabbitMQ**. The services start and their HTTP APIs work
fully — the outbox keeps publishing into each service's own database — but nothing is delivered
between services until a broker is reachable. Concretely: Identity works end to end, and Sanctions
rejects a raise with `request.employee_not_found`, because its directory projection is fed by events
that have not been delivered. Those messages are not lost; they sit in `OutboxMessage` and flush once a
broker appears.

Create the databases:

```bash
dotnet tool restore
```

```bash
dotnet dotnet-ef database update --project src/Services/Identity/Identity.Api
```

Repeat for `Sanctions.Api` and `Notifications.Api`. Then run each service with the key and its
connection string supplied through the environment:

```bash
Jwt__Key="<32+ character key>" dotnet run --project src/Services/Identity/Identity.Api --no-launch-profile --urls http://localhost:5101
```

Then `Sanctions.Api` on 5102, `Notifications.Api` on 5103 and `Gateway.Api` on 5100.

> `/health/ready` reports **503** on this path, because MassTransit's bus health check fails without a
> broker. That is correct — the service genuinely cannot do everything it advertises. The HTTP API still
> serves requests.

---

## Path B — infrastructure in Docker, services from your IDE

Better when you are actively changing backend code and want the debugger.

### 1. Start only the infrastructure

```bash
JWT_KEY=unused docker compose up sql rabbitmq
```

### 2. Set the signing key once per service

```bash
dotnet user-secrets set "Jwt:Key" "local-dev-key-at-least-32-characters-long" --project src/Services/Identity/Identity.Api
```

Repeat for `Sanctions.Api`, `Notifications.Api` and `Gateway.Api`. All four must get the **same** value.

### 3. Point the services at the container's SQL Server

The default connection strings target LocalDB. Either edit `appsettings.Development.json` per service, or
export overrides:

```bash
export ConnectionStrings__IdentityDb="Server=localhost,1433;Database=DisciplinaryMeasures.Identity;User Id=sa;Password=Your_strong_Passw0rd;TrustServerCertificate=True"
```

### 4. Run the four services

Each in its own terminal:

```bash
dotnet run --project src/Services/Identity/Identity.Api
```

Then `Sanctions.Api`, `Notifications.Api`, `Gateway.Api`. The gateway's default configuration already
points at `localhost:5101/5102/5103`.

---

## Creating the first account

Registration always produces a **`Guest` whose account is `Pending`** — it cannot sign in until an
administrator activates it. On a fresh database there is no administrator, so the first account has to be
promoted directly in the database.

Register through the UI at http://localhost:5173/register, or:

```bash
curl -X POST http://localhost:5100/api/authentication/register -H "Content-Type: application/json" -d '{"id":"EMP001","firstName":"Amina","lastName":"Haddad","email":"amina@company.com","password":"passw0rd1","cin":null,"phoneNumber":null,"address":null}'
```

Then promote and activate it:

```bash
docker compose exec sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "Your_strong_Passw0rd" -C -d DisciplinaryMeasures.Identity -Q "UPDATE Users SET Role='Administrator', AccountStatus='Active' WHERE Id='EMP001'"
```

Sign in at http://localhost:5173/login.

> **Note:** this is a bootstrap gap, not a designed flow. A seeding step that creates the first
> administrator on an empty database would be the proper fix.

### Then, to get a working sanction request

The approval chain comes from supervisor relationships, so a request needs at least two people:

1. Create a second employee under **Users → Add user** and set their **supervisor** to the first.
2. Sign in as the first user and raise a request against the second.
3. The request routes to the second user's supervisor chain.

An employee with no supervisor cannot be the subject of a request — there would be nobody to validate it,
and the API rejects it with `request.no_chain`.

---

## Useful URLs

| What | URL |
|---|---|
| Frontend | http://localhost:5173 |
| Gateway | http://localhost:5100 |
| Identity API reference | http://localhost:5101/scalar/v1 |
| Sanctions API reference | http://localhost:5102/scalar/v1 |
| Notifications API reference | http://localhost:5103/scalar/v1 |
| RabbitMQ management | http://localhost:15672 — guest / guest |

The RabbitMQ console is the useful one when something looks wrong: **Queues** shows whether messages are
being consumed, and an `_error` queue with anything in it means a consumer is failing.

---

## Troubleshooting

**Everything returns 502 through the gateway.**
The services are still starting, or failed. `docker compose ps` shows status; `docker compose logs identity`
shows why.

**Services restart in a loop with a login failure.**
SQL Server is still initialising. The retry loop handles this, but if it persists past a couple of
minutes, check `docker compose logs sql`.

**The browser reports a CORS error.**
The frontend is pointed at a service instead of the gateway. `VITE_API_BASE_URL` must be
`http://localhost:5100`. Only the gateway sends CORS headers.

**Sign-in returns 403 with "awaiting activation".**
Working as intended — the account has not been activated. See
[Creating the first account](#creating-the-first-account).

**Raising a request fails with `request.no_chain`.**
The employee has no supervisor set, so there is nobody to validate. Set one under Users.

**Notifications never arrive.**
Check RabbitMQ's management console for an `_error` queue. Note that notifications are also polled every
30 seconds by the frontend, so they should appear even if the live push is not working.

---

## Resetting everything

Stops the containers and deletes the database volume:

```bash
docker compose down -v
```

The next `docker compose up` starts from empty databases and re-applies migrations.

---

## Running the tests

Backend:

```bash
dotnet test
```

Frontend, in its own repository:

```bash
npm test
```

Neither needs Docker — the backend tests use EF Core's in-memory provider and substitute the broker, and
the frontend tests mock HTTP at the network layer.
