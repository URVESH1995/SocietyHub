# SocietyHub — Delivery Roadmap

Living task tracker. Update the status marker in place as work lands; this file is the
source of truth, not any external board.

**Legend** — `[x]` done · `[~]` in progress · `[ ]` pending · `[!]` blocked

| Phase | Scope | Done | Total | Progress |
| --- | --- | --- | --- | --- |
| **0** | Foundation | 24 | 24 | 100% |
| **1** | Daily operations | 26 | 65 | 40% |
| **2** | Bulk service drives | 0 | 22 | 0% |
| **3** | Vision and AI security | 0 | 39 | 0% |
| **4** | Production hardening | 0 | 20 | 0% |
| **5** | Backlog | 0 | 12 | 0% |
| | **Total** | **50** | **182** | **27%** |

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
- [ ] `P1-27` `VisitPass` aggregate with OTP and QR
- [ ] `P1-28` Resident pre-approval flow
- [ ] `P1-29` Check-in / check-out
- [ ] `P1-30` Delivery and cab entry, left-at-gate handling
- [ ] `P1-31` Daily help attendance — punch in/out, monthly sheet
- [ ] `P1-32` Blacklist
- [ ] `P1-33` SOS incident capture and fan-out
- [ ] `P1-34` Visitor photo upload to blob with short-lived SAS URLs
- [ ] `P1-35` Gate log monthly partitioning
- [ ] `P1-36` Offline sync endpoint for the guard app

### Helpdesk service
- [ ] `P1-37` `Complaint` aggregate and ticket numbering
- [ ] `P1-38` Categories and priority → SLA due date in **society-local time**
- [ ] `P1-39` Assignment and status workflow
- [ ] `P1-40` SLA sweeper background service
- [ ] `P1-41` Escalation matrix to committee on breach
- [ ] `P1-42` Resident rating on close
- [ ] `P1-43` Photo and document attachments

### Notification service
- [ ] `P1-44` Template store, `en-IN` and `hi-IN`
- [ ] `P1-45` Channel providers — push, SMS, email, WhatsApp behind one abstraction
- [ ] `P1-46` Consumers for every Phase 1 event
- [ ] `P1-47` Priority lanes and quiet hours in society-local time
- [ ] `P1-48` Delivery log with retry and dead-letter
- [ ] `P1-49` Per-user notification preferences and opt-out

### Notice service
- [ ] `P1-50` Notice board, targeting by tower or flat
- [ ] `P1-51` Polls and voting

### Client applications
- [ ] `P1-52` Shared Razor class library — components and view models
- [ ] `P1-53` Generated API client SDK from OpenAPI
- [ ] `P1-54` Resident app — MAUI Blazor Hybrid, iOS and Android
- [ ] `P1-55` Guard app — Android tablet, offline-capable, camera and QR scan
- [ ] `P1-56` Admin and committee web — Blazor WASM PWA
- [ ] `P1-57` Localisation resources and in-app language switcher

### Testing and CI
- [ ] `P1-58` Testcontainers integration harness (SQL Server + RabbitMQ + Redis)
- [ ] `P1-59` Contract tests for integration events
- [ ] `P1-60` CI pipeline — build, test, tenancy gate as a required check

### Shipping over years — required for yearly feature releases
- [ ] `P1-61` `IFeatureGate` implementation with per-society overrides, Redis-cached
- [ ] `P1-62` Subscription plans — Basic / Standard / Premium mapped to feature sets
- [ ] `P1-63` Canary rollout — enable a feature for N societies, watch, then widen
- [ ] `P1-64` `/features` endpoint so clients hide what a society does not have
- [ ] `P1-65` API versioning strategy, with a deprecation policy for old mobile builds

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

Phase 1 has one ordering constraint that matters. **`P1-01` through `P1-10` block almost
everything else** — outbox, idempotency, messaging lanes and auth are the substrate the four
domain services are written against. Building a domain service first and retrofitting the
outbox means rewriting every publish call site.

Recommended order:

```
P0-23 (Docker)  →  P1-01..P1-10 (cross-cutting)  →  P1-11..P1-19 (Identity)
   →  P1-20..P1-26 (Society)  →  P1-27..P1-36 (Gate)  →  P1-37..P1-43 (Helpdesk)
   →  P1-44..P1-49 (Notification)  →  P1-52..P1-57 (Clients)
```

Society precedes Gate because Gate resolves flats from it. Notification comes after the
services that publish the events it consumes, so its templates are written against real
payloads rather than guesses.
