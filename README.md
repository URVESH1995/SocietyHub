# SocietyHub

A society and apartment management platform built as a .NET 10 microservices system.

Residents log visitors at the gate, raise complaints against a 24-hour resolution promise,
and pool demand across flats to buy household services — AC servicing, deep cleaning, car
washing — at bulk rates no single flat could negotiate alone.

The bulk-buying engine is the part worth reading. It is a distributed saga: a drive opens,
flats enrol, the price drops as a quorum builds, and if the cut-off passes below quorum the
whole thing compensates and refunds. That is a genuinely hard coordination problem, and it
is the reason this repository is more than a CRUD demo.

---

## Status

**Phase 0 — foundation. Complete and building.**

| Component | State |
| --- | --- |
| Solution, central package management, build props | Done |
| `SharedKernel` — entity, aggregate, result, tenant primitives | Done |
| `Contracts` — integration event catalogue | Done |
| `ServiceDefaults` — OpenTelemetry, health, resilience, discovery | Done |
| Aspire AppHost — SQL Server, Redis, RabbitMQ, service graph | Done |
| API Gateway — YARP, rate limiting, service discovery | Done |
| Identity service — skeleton with live dependency health checks | Done |
| Tenant isolation — query filters, write guard, convention tests | Done, 16 tests passing |
| Globalisation primitives — `LanguageTag`, `Money`, `PhoneNumber` | Done |
| Domain logic | Phase 1 |

**Scale targets:** ~170 societies · 100,000 users · 500 RPS sustained, 1,500 burst ·
99.9% availability, with SOS and gate entry held to a higher bar.

---

## Running it

**Prerequisites:** .NET 10 SDK, Docker Desktop, and the Aspire CLI
(`dotnet tool install -g Aspire.Cli`).

```bash
aspire run
```

That starts SQL Server, Redis and RabbitMQ as containers, launches the gateway and the
Identity service, wires every connection string and OTLP endpoint between them, and opens
the Aspire dashboard with live logs, traces and metrics.

To run only the backing services — for integration tests, or to debug a single service from
your IDE against real infrastructure:

```bash
docker compose -f deploy/compose/docker-compose.infra.yml up -d
```

Copy `deploy/compose/.env.example` to `.env` and set the passwords first.

---

## The services we offer residents

Distinct from the microservices below: this is the product catalogue.

**Daily use, no vendor involved.** Visitor and gate passes, daily-help attendance,
complaints against a 24-hour SLA, the notice board, and an SOS panic button.

**Bulk service drives — the differentiator.** One service, one society, one date window,
and a price that falls as more flats join.

| Category | Examples |
| --- | --- |
| Appliance | AC service, washing machine, refrigerator, chimney, geyser, water purifier |
| Home cleaning | Full-flat deep clean, kitchen, bathroom, sofa and mattress, water tank |
| Vehicle | Car foam wash, interior detailing, bike wash, monthly subscription |
| Pest and sanitation | Cockroach, termite, mosquito mesh, disinfection |
| Home repair | Plumbing, electrical, carpentry, painting, waterproofing |
| Society-level | Lift AMC, DG set, fire extinguisher refill, common-area painting, gardening |

A drive prices on slabs — 1–9 units at one rate, 10–24 cheaper, 25–49 cheaper again — and
residents watch a live counter: *twelve flats joined, thirteen more to unlock the next
rate*. A minimum quorum and a cut-off date bound the commitment; below quorum the drive
cancels and refunds automatically.

---

## The microservices

Each owns its data outright. No service reads another's database.

| Service | Owns | Phase |
| --- | --- | --- |
| `Identity.Api` | Users, roles, refresh tokens, JWT issuance | 1 |
| `Society.Api` | Society, tower, floor, flat, residents, vehicles, parking | 1 |
| `Gate.Api` | Visitors, passes, OTP, staff attendance, SOS incidents | 1 |
| `Helpdesk.Api` | Complaints, SLA clock, assignment, escalation | 1 |
| `Notification.Api` | Email, SMS, push and WhatsApp fan-out | 1 |
| `ApiGateway` | Routing, rate limiting, CORS | 1 |
| `Vendor.Api` | Vendors, KYC, rate cards, technicians | 2 |
| `ServiceDrive.Api` | Catalogue, drives, enrolment, quorum, saga | 2 |
| `Scheduling.Api` | Slots, technician assignment, job lifecycle | 2 |
| `Payment.Api` | Orders, gateway integration, refunds, ledger | 2 |
| `Notice.Api` | Notices, polls, documents | 3 |
| `Reporting.Api` | Read-model projections, dashboards | 3 |

The split follows load, not tidiness. Gate is write-heavy in two sharp daily spikes,
Society is read-heavy and almost entirely cacheable, and Notification is bursty and must
never block a request. Those are different scaling problems, which is the honest
justification for separate processes.

---

## How services talk

**Synchronously** only when a query must be fresh, over HTTP through service discovery,
with Polly retry and circuit breaking from `ServiceDefaults`.

**Asynchronously** for everything that changes state elsewhere, over RabbitMQ. Publishers
write to a transactional outbox in the same transaction as the state change, so a fact and
the message announcing it can never disagree. Consumers deduplicate on `EventId`.

---

## Multi-tenancy

Pooled: every society shares a database per service, separated by a `SocietyId` column, with
a routing table so any single society can later be promoted to its own database without a
code change. At ~170 societies the alternative — a database per tenant — would mean 850
databases and 850 migrations per release.

Pooled isolation is only as strong as its enforcement, so there are five independent layers.
Each one alone has a realistic bypass; together they have none.

| Layer | Mechanism | Catches |
| --- | --- | --- |
| 1 | EF Core query filter, applied automatically to any `ITenantScoped` entity | A forgotten `WHERE` on a read |
| 2 | `TenantGuardInterceptor` on `SaveChanges` | Cross-tenant **writes** |
| 3 | Model convention tests | A new entity added without tenant scoping |
| 4 | Two-society integration tests | Behavioural regression, gated in CI |
| 5 | SQL Server row-level security | Raw SQL, and bugs in layers 1–4 |

Layer 2 is the one that is usually missing. **A query filter constrains `SELECT` and does
nothing at all about `INSERT` or `UPDATE`** — so a handler that copies a caller-supplied
`SocietyId` onto an entity writes into another society's data while every read-side test
still passes. The interceptor stamps inserts with the caller's society and refuses anything
carrying a different one, including an attempt to move an existing row between societies.

The tenant comes from a signed JWT claim and from nowhere else — never a route value, query
string, header or request body, since any of those makes tenancy a decision the caller
controls. With no valid claim the filter resolves to `Guid.Empty` and matches nothing: the
default is deny, not deny-if-someone-remembered-to-check.

---

## Localisation and global readiness

The app ships in ten Indian languages and is built to leave India without a migration.

| Concern | Decision |
| --- | --- |
| UI strings | Client-side resource bundles. The API returns **error codes**, not sentences — `Error.Code` exists for exactly this. |
| Notification templates | Server-side and localised per recipient. A resident who chose Tamil gets Tamil SMS. |
| Language resolution | Resident preference → `Accept-Language` → society default → platform default |
| Phone numbers | **E.164 always**, `+919876543210`. A bare ten-digit column is the most expensive schema mistake available here — phone is login identity, OTP destination and visitor identifier at once. |
| Money | `Money` = amount + ISO 4217 code. Never a bare `decimal`. |
| Time | Stored UTC; the 24-hour SLA, escalation windows and quiet hours are judged in **society-local** time via `ILocaleContext.TimeZone`. |
| Text | `nvarchar` throughout, ICU enabled. `InvariantGlobalization` is explicitly **off** — it would break Devanagari collation. |

---

## Stack

ASP.NET Core 10 minimal APIs · EF Core 10 · YARP · MassTransit 8.5 on RabbitMQ ·
Redis · SQL Server 2022 · OpenIddict · OpenTelemetry into Jaeger, Prometheus and Grafana ·
.NET Aspire for local orchestration · xUnit with Testcontainers · Azure Container Apps.

---

## Layout

```
src/
  BuildingBlocks/
    SocietyHub.SharedKernel/      domain primitives, Result, tenant abstractions
    SocietyHub.Contracts/         integration events — the public wire contract
    SocietyHub.ServiceDefaults/   telemetry, health, resilience, discovery
  Aspire/SocietyHub.AppHost/      the local topology, and the deployment source of truth
  Gateway/SocietyHub.ApiGateway/  YARP
  Services/<Context>/             one folder per bounded context
deploy/compose/                   infrastructure-only Compose stack
docs/                             architecture decisions
tests/
```

---

## Roadmap

**Phase 1 — daily operations.** Identity, Society, Gate, Helpdesk, Notification behind the
gateway. Real JWTs, real events, a working SLA escalation sweeper.

**Phase 2 — bulk drives.** Vendor, ServiceDrive, Scheduling, Payment. The saga, the outbox,
idempotent consumers, compensation on quorum failure.

**Phase 3 — production.** Grafana dashboards, alerting, load tests, CI/CD, and deployment to
Azure Container Apps.
