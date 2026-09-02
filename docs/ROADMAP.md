# SocietyHub — Delivery Roadmap

Living task tracker. Update the status marker in place as work lands; this file is the
source of truth, not any external board.

**Legend** — `[x]` done · `[~]` in progress · `[ ]` pending · `[!]` blocked

| Phase | Scope | Done | Total | Progress |
| --- | --- | --- | --- | --- |
| **0** | Foundation | 24 | 24 | 100% |
| **1** | Daily operations | 65 | 65 | 100% |
| **2** | Bulk service drives | 0 | 22 | 0% |
| **3** | Vision and AI security | 0 | 39 | 0% |
| **4** | Production hardening | 0 | 20 | 0% |
| **5** | Backlog | 0 | 12 | 0% |
| | **Total** | **89** | **182** | **49%** |

---

## Where the project stands

**Phase 1 is complete** as of 2 September 2026 — all 65 tasks. What exists is a backend that
runs end to end and three client apps that build, with the admin console verified in a browser
against the live stack.

Two things in the mobile apps are code-complete but **unverified on hardware**, and are listed
honestly under "what has not been verified" in [CLIENTS.md](CLIENTS.md): the camera scan path
and push delivery. Push additionally needs a Firebase project, which is the customer's to
supply — the app builds and runs without it and simply receives no push.

| | Count |
| --- | --- |
| Services | 6 — Identity, Society, Gate, Helpdesk, Notification, Notice |
| Client apps | 3 — Admin web (Blazor WASM PWA), Resident (MAUI), Guard (MAUI) |
| Building blocks | 8 — SharedKernel, Contracts, Persistence, Messaging, Caching, Web, Features, ServiceDefaults |
| Tests | **319 passing**, 4 of them against real containers |
| Release build | Clean with `-warnaserror`, zero warnings |

### What has actually been proven, and what has only been built

The distinction matters more than the task count. A ticked box means the code exists and its
unit tests pass; it does not by itself mean anyone has watched it work.

| Claim | Evidence |
| --- | --- |
| Tenant isolation holds | **Proven.** 16 unit tests plus 4 integration tests against real SQL Server 2022 — filtered SQL, cross-tenant reads, rowversion concurrency, Devanagari collation. Layer 5 (row-level security) separately proven against a live instance, including raw SQL that bypasses EF. |
| The topology boots | **Proven** (`P0-23`). SQL Server, Redis and RabbitMQ healthy; the gateway routes to Identity through YARP service discovery. |
| Domain rules are correct | **Unit-tested.** SLA clocks, quiet hours, poll quorum, notification cost policy, entitlement resolution, the offline queue. |
| The six services run together end to end | **Proven**, 2 September 2026. All six start under Aspire, the gateway routes to every one of them, and `/api/v1/{service}/info` returns 200 through it. Getting there took six fixes — see the commit for what was actually broken. |
| Anything renders in a browser | **Proven, end to end.** Sign in with a phone and a one-time code, in English or Hindi, against the live stack — verified for the resident, admin and guard demo users. Feature-gated navigation populates from the real entitlement manifest, and a guard is turned away from the committee console with an explanation. |
| Anything renders on a phone | **Not yet.** Both MAUI apps build for Android and their logic is unit-tested, but no screen has been opened on hardware. The camera scan and push delivery specifically cannot be proven any other way — an emulator has no real camera and issues no push token. |

### What Phase 1 leaves behind

Nothing in Phase 1 is outstanding. Two things are worth carrying forward rather than
forgetting:

- **Hardware verification.** The camera scan path and push delivery are code-complete and
  build, but no screen has been opened on a real device. Listed in detail under "what has not
  been verified" in [CLIENTS.md](CLIENTS.md).
- **Two launch prerequisites live in Phase 4**, not year two: dashboards and alerting
  (`P4-01`, `P4-02`), and the DPDP compliance work (`P4-12`). You cannot operate a 99.9% SLO
  without the first or legally launch in India without the second.

---

## How to verify

Three levels, cheapest first. Each answers a different question.

### 1. Does the code do what it claims? (no Docker, ~10 seconds)

```bash
dotnet test SocietyHub.slnx
```

319 tests. Four report as skipped without Docker; that is expected and correct.

### 2. Is the isolation real against a real database? (Docker required, ~2 minutes)

Start Docker Desktop, then:

```bash
dotnet test tests/SocietyHub.IntegrationTests
```

Starts SQL Server 2022, RabbitMQ and Redis in containers and runs the four tests that cannot
be faked by SQLite. `Skipped: 0` in the output is what tells you they actually ran.

### 3. Can I see it? (Docker required)

```bash
./scripts/run.sh
```

Brings up the full stack and opens the **Aspire dashboard** — a live view of all six services,
their health, logs, traces and the RabbitMQ queues. This is the first real UI, and it is a
genuine one: it shows the priority lanes with messages moving through them.

Each service also serves interactive API documentation at `/scalar/v1`, where you can execute
requests against the running system without writing any client code.

The admin console runs separately:

```bash
dotnet run --project src/Clients/SocietyHub.Admin.Web
```

Sign in with any seeded demo user. A development build returns the one-time code on the wire
and shows it on screen, so no telecom account is needed:

| Phone | Signs in as |
| --- | --- |
| `9000000001` | Demo Admin — the full console |
| `9000000002` | Amit Sharma, resident |
| `9000000003` | Ramesh, guard — refused, with an explanation |

The last row is the interesting one: the console admits only society administrators and
committee members, because a guard has the gate tablet and a resident has the mobile app.

See [RUNNING.md](RUNNING.md) for prerequisites and [CLIENTS.md](CLIENTS.md) for building the
mobile apps.

---

## Locked decisions

| Decision | Choice |
| --- | --- |
| Framework | .NET 10 (LTS) |
| Local orchestration | .NET Aspire 13.5.2, with a Compose fallback |
| Cloud | Azure Container Apps, single region, multi-AZ |
| Tenancy | Pooled, with a per-society promotion path |
| Availability SLO | 99.9% general · 99.95% gate · 99.99% SOS |
| Languages at launch | English (`en-IN`), Hindi (`hi-IN`), society-configurable default |
| Compliance | India DPDP first, region-pluggable for global later |
| Messaging | MassTransit 8.5.x (Apache-2.0), RabbitMQ, priority lanes |
| Clients | Shared Razor class library → MAUI Blazor Hybrid (mobile) + Blazor WASM PWA (web) |
| Camera AI | Edge inference per society; only events reach the cloud, footage never does |
| Face recognition | Full coverage — residents, staff, visitors, watchlist. Matches alert a human; nothing auto-acts |

---

## Release plan

Phases are how the work is grouped. Releases are what a society actually receives. They are
not the same thing, and conflating them is how a roadmap turns into a two-year silence.

| Release | Contents | Target |
| --- | --- | --- |
| **v1.0** | Visitors, gate passes, delivery entry, daily-help attendance, complaints with the 24-hour SLA, notices, SOS, all three clients | Launch |
| **v1.1** | Deferred polish — polls, blacklist, WhatsApp channel, complaint attachments, directory privacy controls | +3 months |
| **v1.5** | **Bulk service drives** — vendors, quorum saga, scheduling, payments | +6 months |
| **v2.0** | **Vision wave 1** — ANPR, tailgating, parking occupancy, intrusion, camera health | Year 2 |
| **v2.5** | **Vision wave 2** — fire, fall, pool safety; face recognition across residents, staff, visitors and watchlist | Year 2 H2 |
| **v3.0** | Maintenance billing, amenity booking, parking management, committee voting, document vault | Year 3 |

### What v1.0 deliberately excludes

Bulk drives, payments and every camera feature. The reasoning is ordering, not value:
gate and complaints are table stakes that get societies onto the platform, and bulk drives
are what makes them stay — but drives need a resident base to reach quorum, so they cannot
come first. Vision needs hardware in buildings you do not yet have contracts with.

### Phase 4 is not a release — and half of it blocks v1.0

Worth stating plainly, because the roadmap ordering implies otherwise. These hardening
tasks are **launch prerequisites**, not year-two work:

`P4-01` `P4-02` dashboards and alerting · `P4-03` load test · `P4-09` row-level security ·
`P4-10` Key Vault · `P4-12` DPDP compliance · `P4-14` security review · `P4-15` Azure
deployment · `P4-16` expand–contract migrations · `P4-17` CI/CD · `P4-18` backup and DR

You cannot operate a 99.9% SLO without dashboards, and you cannot legally launch in India
without the DPDP work. The remaining Phase 4 tasks — archival, photo lifecycle, reporting,
region-pluggable residency — genuinely can wait.

---

## Phase 0 — Foundation

**Goal:** a walking skeleton that boots, is observable, and cannot leak data across societies.

### Solution and build
- [x] `P0-01` Solution structure (`.slnx`), folder layout
- [x] `P0-02` Central package management (`Directory.Packages.props`)
- [x] `P0-03` Shared build props, `global.json` SDK pin
- [x] `P0-04` `.gitignore`, `.editorconfig`

### Shared kernel
- [x] `P0-05` `Entity`, `AggregateRoot`, domain events, rowversion concurrency
- [x] `P0-06` `Result` / `Error` / `ErrorType` pattern
- [x] `P0-07` Tenancy abstractions — `ITenantScoped`, `ITenantContext`, claims, violation exception
- [x] `P0-08` Auditing interfaces — `IAuditable`, `ISoftDeletable`
- [x] `P0-09` Globalisation primitives — `LanguageTag`, `Money`, `PhoneNumber`, `ILocaleContext`

### Contracts
- [x] `P0-10` `IntegrationEvent` base with `SocietyId` and `EventId`
- [x] `P0-11` Event catalogue — identity, society, gate, helpdesk

### Infrastructure and platform
- [x] `P0-12` `ServiceDefaults` — OpenTelemetry, resilience, service discovery
- [x] `P0-13` Health endpoints hardened for Container Apps probes
- [x] `P0-14` Aspire AppHost topology — SQL Server, Redis, RabbitMQ, service graph
- [x] `P0-15` API Gateway — YARP, service discovery, rate limiting, forwarded headers
- [x] `P0-16` Identity API skeleton with live dependency health checks
- [x] `P0-17` Compose infrastructure stack + Prometheus, Jaeger, Grafana

### Tenant isolation
- [x] `P0-18` `TenantDbContext` — auto-applied named query filters (layer 1)
- [x] `P0-19` `TenantGuardInterceptor` — write-side guard (layer 2)
- [x] `P0-20` `AuditInterceptor` — stamping and soft-delete downgrade
- [x] `P0-21` Request contexts — `HttpTenantContext`, `HttpCurrentUser`, `HttpLocaleContext`
- [x] `P0-22` Tenancy test suite — 16 tests, mutation-verified (layers 3 and 4)

### Verified against live infrastructure
- [x] `P0-23` **Full topology boots** — SQL Server, Redis, RabbitMQ healthy; gateway routes to Identity through YARP service discovery
- [x] `P0-24` **Layer 5 — SQL Server row-level security**, proven on SQL Server 2022: filtered reads, blocked cross-tenant insert and row-move, default-deny with no session context, and isolation holding for raw SQL that bypasses EF entirely

---

## Phase 1 — Daily operations

**Goal:** visitors, complaints and notifications working end to end for real societies.

### Cross-cutting — build first, everything else depends on it
- [x] `P1-01` **Transactional outbox** — staging, dispatcher, ordering, backoff, poisoning
- [x] `P1-02` **Inbox / idempotent consumer** — composite-key dedup, claim commits with the handler
- [x] `P1-03` **MassTransit priority lanes** — Critical / Gate / Normal / Bulk, one queue each
- [x] `P1-04` **`Idempotency-Key` middleware** — society-and-user scoped replay, in-flight lock
- [x] `P1-05` **`Result` → `ProblemDetails`** with machine-readable codes, plus exception handler
- [x] `P1-06` **FluentValidation pipeline** — per-endpoint filter, field-level codes
- [x] `P1-07` **JWT authentication** — validated independently by every service, 30s clock skew
- [x] `P1-08` **Authorisation policies** for the seven roles, deny-by-default fallback
- [x] `P1-09` **Redis cache** — tenant-scoped keys by construction, degrades to a miss on outage
- [x] `P1-10` **Redis distributed lock** — token-checked release, honest about its limits

### Identity service
- [x] `P1-11` **ASP.NET Identity** with EF Core stores; a person is global, their standing is scoped
- [x] `P1-12` **Token issuance** behind `ITokenIssuer` — direct JWT, not OpenIddict (see note below)
- [x] `P1-13` **Refresh rotation with reuse detection** — token families; the whole family is revoked on replay
- [x] `P1-14` **Seven roles**, held per society on `SocietyMembership` rather than globally
- [x] `P1-15` **`society_id` claim** and society switching for multi-society residents
- [x] `P1-16` **Phone OTP** — salted hash, 3-attempt cap, per-phone and per-IP limits that fail closed
- [x] `P1-17` **Guard device identity** — device and guard are separate identities; shift PIN with lockout
- [x] `P1-18` **`UserRegistered` through the outbox**, committed with the membership that caused it
- [x] `P1-19` **Migrations and role seed data**

> **Deviation on `P1-12`.** OpenIddict was replaced with direct JWT issuance behind
> `ITokenIssuer`. Its value is standards-compliant OAuth2/OIDC for third-party clients, a
> Phase 5 concern — v1.0 has three first-party clients and a phone-OTP sign-in that is not a
> standard grant in any case. OpenIddict slots in behind the same interface when the public
> API arrives, without touching a call site.

### Society service
- [x] `P1-20` **Society / Tower / Flat** — the society is its own tenant row, so one filter covers its profile too
- [x] `P1-21` **Residents** — owner, tenant, family; occupancy and primary contact both derived
- [x] `P1-22` **Vehicles and parking** — registrations normalised for the Phase 3 ANPR match
- [x] `P1-23` **Society settings** — default language, time zone, currency, country
- [x] `P1-24` **Onboarding** — society creation and forgiving bulk flat import
- [x] `P1-25` **Directory with privacy controls** — minimum by default, phone is opt-in
- [x] `P1-26` **`ResidentRegistered` and `FlatOccupancyChanged`** through the outbox, only on real change

### Gate service
- [x] `P1-27` **`VisitPass`** with a hashed, fixed-time-compared gate code and a 5-attempt cap
- [x] `P1-28` **Resident pre-approval** — blacklist checked at issue, window capped at 24h
- [x] `P1-29` **Check-in / check-out** — one visit per pass; both ends tracked, never inferred
- [x] `P1-30` **Walk-up delivery and cab entry**, with left-at-gate modelled explicitly
- [x] `P1-31` **Daily help attendance** — badge punch, monthly sheet on society-local dates
- [x] `P1-32` **Blacklist** — mandatory reason, named author, forced review date
- [x] `P1-33` **SOS** on the Critical lane, with time-to-acknowledge recorded
- [x] `P1-34` **Visitor photos** — private container, short-lived SAS, society-checked keys
- [x] `P1-35` **Gate log partitioning** — `yyyyMM` stamped at capture, leading the index
- [x] `P1-36` **Offline sync** — device-generated ids dedupe; capture time preserved

### Helpdesk service
- [x] `P1-37` **`Complaint` aggregate** with gapless per-society ticket numbering
- [x] `P1-38` **SLA on working hours** in society-local time; urgent categories auto-escalate
- [x] `P1-39` **Assignment and status workflow** — assign, start, resolve, close, reopen, reject
- [x] `P1-40` **SLA sweeper** — batched, cooldown-limited, longest-overdue first
- [x] `P1-41` **Escalation matrix** — assignee, admin, committee; level 3 gets a faster lane
- [x] `P1-42` **Rating on close** — only the resident who raised it may close
- [x] `P1-43` **Notes and attachments**, with internal-only notes separated from resident-visible

### Notification service
- [x] `P1-44` **Template store** in `en-IN` and `hi-IN`, complete in both by test
- [x] `P1-45` **Channel providers** behind one abstraction — push, SMS, email, in-app
- [x] `P1-46` **Consumers** for the gate, SOS and complaint events, on their lanes
- [x] `P1-47` **Priority lanes and quiet hours** in society-local time; Critical never held
- [x] `P1-48` **Delivery log** with exponential backoff and dead-lettering
- [x] `P1-49` **Per-user preferences** and opt-out, which Critical overrides

### Notice service
- [x] `P1-50` **Notice board** with targeting by tower, flat or committee, and read receipts
- [x] `P1-51` **Polls and voting** — one vote per flat, frozen quorum, sealed resolutions

### Client applications
- [x] `P1-52` **Shared Razor class library** — components, view models, API client, offline queue
- [x] `P1-53` **API client SDK** — hand-written, with an OpenAPI drift-check script (see note)
- [x] `P1-54` **Resident app** — MAUI Blazor Hybrid, sign-in and FCM push registration; needs your Firebase project
- [x] `P1-55` **Guard app** — Android tablet, offline queue, sign-in, camera QR scan beside typed entry
- [x] `P1-56` **Admin and committee web** — Blazor WASM PWA, feature-gated navigation
- [x] `P1-57` **Localisation** — en-IN and hi-IN resources, parity-tested, native-script switcher

### Testing and CI
- [x] `P1-58` **Testcontainers harness** — real SQL Server, RabbitMQ and Redis; skips without Docker
- [x] `P1-59` **Contract tests** for every integration event — shape, JSON round trip, uniqueness
- [x] `P1-60` **CI pipeline** — build with `-warnaserror`, plus a separate tenancy gate

### Shipping over years — required for yearly feature releases
- [x] `P1-61` **`IFeatureGate`** — society override beats plan, Redis-cached, baseline on outage
- [x] `P1-62` **Subscription plans** — Basic / Standard / Premium, with a lapse path
- [x] `P1-63` **Canary rollout** — pilot by name, then a stable hashed percentage, then all
- [x] `P1-64` **`/api/features`** manifest so clients hide what a society does not have
- [x] `P1-65` **API versioning** at the gateway, plus a client-build deprecation gate

---

## Phase 2 — Bulk service drives

**Goal:** the group-buying saga, end to end, with money moving.

### Vendor service
- [ ] `P2-01` Vendor aggregate, onboarding, KYC
- [ ] `P2-02` Rate cards with slab pricing
- [ ] `P2-03` Technician roster and coverage areas
- [ ] `P2-04` Vendor ratings and performance history

### Service catalogue and drives
- [ ] `P2-05` Service catalogue with localised names and descriptions
- [ ] `P2-06` `ServiceDrive` aggregate — open, quorum, cut-off
- [ ] `P2-07` Enrolment and live join counter in Redis
- [ ] `P2-08` Distributed lock so concurrent enrolment cannot miscount quorum
- [ ] `P2-09` Slab price recalculation as the counter crosses thresholds
- [ ] `P2-10` **MassTransit saga state machine** for the drive lifecycle
- [ ] `P2-11` Compensation and refund when quorum is missed at cut-off

### Scheduling service
- [ ] `P2-12` Slot definition and capacity
- [ ] `P2-13` Technician assignment
- [ ] `P2-14` Job lifecycle with proof of completion
- [ ] `P2-15` Rescheduling and cancellation

### Payment service
- [ ] `P2-16` Order aggregate and ledger
- [ ] `P2-17` Razorpay integration
- [ ] `P2-18` Webhook handling with idempotency
- [ ] `P2-19` Refunds and partial refunds
- [ ] `P2-20` Vendor payouts and reconciliation

### Verification
- [ ] `P2-21` Saga integration tests including every compensation path
- [ ] `P2-22` Drive UI across all three clients

---

## Phase 3 — Vision and AI security

**Goal:** camera analytics that make a society genuinely safer, without building a
surveillance apparatus nobody consented to.

**Architecture:** inference runs on an edge box inside each society. Centralising 2,700
camera streams would be roughly 5 Gbps sustained and 57 TB/day, so only *events* travel —
a JSON payload plus a ~50 KB thumbnail. Footage stays on the local recorder.

### Edge agent
- [ ] `P3-01` `SocietyHub.Edge.Agent` — .NET 10 worker service skeleton
- [ ] `P3-02` RTSP / ONVIF camera ingest
- [ ] `P3-03` ONNX Runtime inference pipeline
- [ ] `P3-04` Store-and-forward buffer that survives a WAN outage
- [ ] `P3-05` Config and model distribution from cloud, with signed model bundles
- [ ] `P3-06` Heartbeat and health reporting
- [ ] `P3-07` Edge provisioning and enrolment, bound to one society
- [ ] `P3-08` Hardware specification and installation runbook

### Vision service
- [ ] `P3-09` `Vision.Api` scaffold and camera registry
- [ ] `P3-10` Zone configuration per society — gate, lobby, parking, perimeter, terrace
- [ ] `P3-11` Event ingestion endpoint, authenticated by edge device identity
- [ ] `P3-12` Alert rules engine — per-society thresholds and active-hours schedules
- [ ] `P3-13` Thumbnail storage with short-lived signed URLs
- [ ] `P3-14` Clip retrieval, every view audited
- [ ] `P3-15` Camera fleet health and offline alerting

### Models — lowest risk first
- [ ] `P3-16` **ANPR** for Indian plate formats — the highest-value, lowest-consent-risk model
- [ ] `P3-17` Person detection and zone intrusion
- [ ] `P3-18` Loitering detection, tuned per zone
- [ ] `P3-19` **Tailgating** — counts people, identifies nobody
- [ ] `P3-20` Fire and smoke detection
- [ ] `P3-21` Fall detection for elderly residents
- [ ] `P3-22` Crowd gathering detection
- [ ] `P3-23` False-positive tuning and a human feedback loop

### Face recognition — residents, staff, visitors and watchlist

Full coverage. Two constraints are structural rather than procedural: the template vault is
tenant-scoped so cross-society matching is impossible, not merely disallowed; and no match
is ever wired to an automatic consequence — every one raises an alert a human confirms.

- [ ] `P3-24` Resident enrolment with an explicit, revocable consent record
- [ ] `P3-25` Template vault **on the edge box**; embeddings never reach the cloud
- [ ] `P3-26` Matching, plus revocation propagated to every edge within minutes
- [ ] `P3-27` A non-face entry path that always works alongside every subject type
- [ ] `P3-34` Visitor capture, matching and short-retention template vault
- [ ] `P3-35` Staff enrolment with notice, and QR punch as the working alternative
- [ ] `P3-36` **Watchlist matching — alert only.** Never auto-denies, never holds a barrier
- [ ] `P3-37` Per-category retention and automatic purge (resident / staff / visitor / watchlist)
- [ ] `P3-38` **Accuracy and bias evaluation** across demographic slices, with published thresholds
- [ ] `P3-39` Point-of-capture notice on gate devices, plus society signage kit

### Privacy and compliance
- [ ] `P3-28` DPDP notice and physical signage kit for each society
- [ ] `P3-29` Retention policy with automatic purge, hard-capped
- [ ] `P3-30` Access audit log — committee members cannot silently browse residents
- [ ] `P3-31` Data-subject requests: view, export, delete

### Integration
- [ ] `P3-32` Fire and fall routed onto the **SOS priority lane**
- [ ] `P3-33` Vision alerts surfaced in resident, guard and admin clients

---

## Phase 4 — Production hardening

**Goal:** meet the 99.9% SLO, satisfy DPDP, and deploy to Azure.

### Observability and reliability
- [ ] `P4-01` Grafana SLO dashboards per service
- [ ] `P4-02` Alerting rules and error-budget burn tracking
- [ ] `P4-03` Load test to 1,500 RPS burst (k6 or NBomber)
- [ ] `P4-04` Chaos tests — kill RabbitMQ, SQL, Redis; assert degradation not failure
- [ ] `P4-05` SOS SMS fallback path that works when the app does not

### Data lifecycle
- [ ] `P4-06` Gate log archival to cool storage after 12 months
- [ ] `P4-07` Visitor photo lifecycle — compress client-side, tier after 90 days
- [ ] `P4-08` Retention jobs and audited purge

### Security and compliance
- [ ] `P4-09` SQL Server row-level security rollout across all services
- [ ] `P4-10` Azure Key Vault and managed identity; no secrets in config
- [ ] `P4-11` Always Encrypted for phone numbers and vehicle registrations
- [ ] `P4-12` DPDP — consent, purpose limitation, deletion, **visitor as non-user data principal**
- [ ] `P4-13` Immutable audit log for admin actions
- [ ] `P4-14` Security review and penetration test checklist

### Deployment
- [ ] `P4-15` Azure Container Apps deployment via `azd`
- [ ] `P4-16` Expand–contract migration strategy for zero-downtime releases
- [ ] `P4-17` CI/CD to Azure with staged rollout
- [ ] `P4-18` Backup and disaster recovery runbook
- [ ] `P4-19` Reporting service with read-model projections
- [ ] `P4-20` Region-pluggable data residency groundwork

---

## Phase 5 — Backlog

Valuable, deliberately deferred.

- [ ] `P5-01` Maintenance billing and dues
- [ ] `P5-02` Amenity booking — clubhouse, gym, party hall
- [ ] `P5-03` Parking management and visitor parking
- [ ] `P5-04` Document vault
- [ ] `P5-05` Move-in / move-out gate pass
- [ ] `P5-06` Committee e-voting
- [ ] `P5-07` Marathi and Gujarati
- [ ] `P5-08` Remaining planned languages
- [ ] `P5-09` Second Azure region
- [ ] `P5-10` Society-to-society benchmarking
- [ ] `P5-11` Vendor marketplace self-serve onboarding
- [ ] `P5-12` Public API for third-party integrations

---

## Critical path

### Phase 1 — done, and what the ordering taught

`P1-01` through `P1-10` blocked almost everything else: outbox, idempotency, messaging lanes
and auth are the substrate the six domain services are written against. Building a domain
service first and retrofitting the outbox would have meant rewriting every publish call site.
That held — nothing had to be unpicked.

The order actually followed:

```
P0-23 (Docker)  →  P1-01..P1-10 (cross-cutting)  →  P1-11..P1-19 (Identity)
   →  P1-20..P1-26 (Society)  →  P1-27..P1-36 (Gate)  →  P1-37..P1-43 (Helpdesk)
   →  P1-44..P1-49 (Notification)  →  P1-50..P1-51 (Notice)
   →  P1-61..P1-65 (entitlement)  →  P1-58..P1-60 (testing, CI)  →  P1-52..P1-57 (clients)
```

Society preceded Gate because Gate resolves flats from it. Notification came after the
services publishing the events it consumes, so its templates were written against real
payloads rather than guesses. Entitlement was pulled forward ahead of the clients so the apps
could be built feature-gated from the start instead of retrofitted.

One thing worth carrying forward: **everything built and every test passed while no service
could actually start.** Five startup bugs — unregistered messaging, a JWT authority triggering
OIDC discovery, no JWT configuration anywhere, an unmapped tenant property, and no bootstrap
path into a fresh database — were invisible to the entire suite. A green build is not a
running system, and the smoke test that would have caught it still has not been run.

### Phase 2 — the ordering that matters next

```
P2-01..P2-04 (Vendor)  →  P2-05..P2-11 (catalogue, drives, saga)
   →  P2-12..P2-14 (Scheduling)  →  P2-15..P2-18 (Payments)  →  P2-19..P2-22 (verification)
```

The saga (`P2-10`) carries the real risk. Quorum, slab repricing and refunds are one
distributed transaction across four services, and the compensation path — money already taken
when quorum is missed at cut-off — has to be built alongside the happy path rather than after
it. A saga whose compensation is retrofitted is a saga that has already lost someone's money
once.

Payments come late deliberately. A drive that reaches quorum without payment is still worth
demonstrating; a payment integration with no drive behind it is not.

### Before any of Phase 2

Two Phase 1 debts and one Phase 4 prerequisite are worth closing first, because each gets more
expensive with every service added:

1. **Run `scripts/smoke-test.sh` to completion.** Six services, never yet exercised together.
   This is the largest single unknown in the project.
2. **Open both mobile apps on a real device.** They build and their logic is tested, but the
   camera scan and push delivery cannot be proven any other way.
3. **`P4-01` and `P4-02`, dashboards and alerting.** You cannot operate a 99.9% SLO without
   them, and retrofitting instrumentation across ten services costs more than across six.
