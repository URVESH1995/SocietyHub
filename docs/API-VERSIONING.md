# API versioning and the deprecation policy

`P1-65`. The problem this document exists for is not the API — it is the phones.

A resident's Android build can be eighteen months old. The platform cannot force an update,
app-store review means even a fix takes days to reach anyone, and a meaningful share of users
have automatic updates switched off. So every server change has to keep working against every
build still in the wild, indefinitely, unless there is an agreed way to stop.

That is what the two mechanisms below are for. The versioned URL lets the server change; the
client-version gate lets old builds be retired without an outage.

---

## 1. Versioned URLs at the gateway

Every route is reachable two ways:

```
/api/gate/visitors        →  gate-api  /api/visitors
/api/v1/gate/visitors     →  gate-api  /api/visitors
```

**Clients must use the `v1` form.** The unversioned form exists for the smoke test, curl and
internal tooling, and is not a contract.

The version lives at the gateway, not in each service's routes. That is a deliberate trade:

- A service does not carry `v1` in a hundred route strings for a version it has never had to
  leave. Retrofitting `v2` into a service is a routing change at the edge plus whatever
  actually differs, not a rename of every endpoint.
- `v2` can be an entirely different deployment of the same service, or a subset of routes
  pointed at a new one, without either version knowing about the other.
- The cost is that a service cannot serve two versions of one endpoint from one process. When
  that is genuinely needed, the answer is a separate route group inside the service, added
  then — not scaffolding built now for a case that may never arrive.

### What forces a new version

Only a change a working client cannot survive:

- Removing a field, or narrowing what a field accepts
- Changing a field's type or its meaning
- Adding a required request field
- Changing an error code a client branches on

These do **not**:

- Adding an optional request field
- Adding a response field — clients must ignore unknown fields, and the generated SDK does
- Adding an endpoint, or a new enum member in a field documented as extensible
- Any change behind a feature that is off for that society

### Retiring a version

A version is announced as deprecated, then removed no earlier than **twelve months** later.
Twelve because the slowest cohort to update is not the one that ignores prompts — it is the
one on a phone that has stopped receiving app-store updates at all, and those turn over on a
hardware cycle.

While deprecated, responses carry:

```
Deprecation: true
Sunset: Sat, 31 Oct 2026 00:00:00 GMT
Link: <https://docs.societyhub.in/api/v2>; rel="successor-version"
```

---

## 2. The client-version gate

`X-SocietyHub-Client: android/2.4.1` on every request from a first-party client.

Two thresholds per platform, both configured rather than compiled, so retiring a build does
not need a deployment:

| Setting | Effect |
| --- | --- |
| `ClientVersions:MinimumRecommended` | Served normally, plus `Deprecation: true`. The client shows a soft update prompt. |
| `ClientVersions:MinimumSupported` | Refused with **426 Upgrade Required** and a machine-readable `code: client.upgrade_required`. |

The gap between them is the only thing that makes retirement survivable. A single hard
cut-off means whoever opens the app the morning it lands experiences an outage they cannot
fix from where they are standing — a guard at a gate, at 6am, with a queue of vehicles.
Recommended is raised first and left there for at least a month before Supported follows.

Implementation notes that are load-bearing:

- **The gate runs before authentication.** An unsupported build gets an actionable 426 rather
  than a 401 it will report to the user as a login problem.
- **`/health` and `/alive` are never gated.** A version rule that can fail a liveness probe is
  a version rule that can take down the platform.
- **A missing header is allowed through by default.** Curl, the smoke test, integration tests
  and partner scripts have no client version, and none of them is an out-of-date phone.
- **A malformed header is allowed through.** It is far more likely to be a proxy mangling the
  value than an attack, and refusing would break a working client over a formatting detail.

### Guard devices are the exception that needs care

A guard tablet is shared, wall-mounted, often on a poor connection, and the person using it
has no authority to update it. Retiring a guard build strands a gate.

So the guard platform is retired on a longer cycle than the resident app, and never during a
festival week — which in India is when gate traffic peaks and is exactly when a stranded
guard is most costly. The offline queue in the Guard app is what makes this survivable at all:
a tablet that cannot reach the server keeps recording entries, so a bad rollout degrades to a
sync delay rather than a paper register.

---

## 3. What is deliberately not here

**Header or query-string versioning.** Both hide the version from logs, from the gateway's
routing table, and from anyone reading a URL in a bug report. A version you cannot see in the
access log is a version nobody can reason about during an incident.

**Semantic versioning of the API itself.** `v1`, `v2` — integers. A client that has to parse
`1.4.2` to decide whether it is compatible will get it wrong, and the only question that
matters is "does my code still work", which is binary.
