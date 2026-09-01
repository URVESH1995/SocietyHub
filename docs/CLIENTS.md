# Client applications

`P1-52`–`P1-57`. Three apps, one shared library, and one requirement that shaped all of it:
*"common application for support all devices."*

---

## The shape

```
SocietyHub.Client.Shared        Razor class library — components, view models, API client,
                               localisation, offline queue. No platform dependency.
        │
        ├── SocietyHub.Admin.Web      Blazor WASM PWA          committee and society admins
        ├── SocietyHub.Resident.App   MAUI Blazor Hybrid        residents, Android and iOS
        └── SocietyHub.Guard.App      MAUI Blazor Hybrid        guards, Android tablet
```

One shared library rather than three codebases is the whole point. A notice card exists once,
so the three apps cannot disagree about what a pinned notice looks like or which language a
resident sees. The screens are Razor components in every app — MAUI Blazor Hybrid renders the
same components inside a native shell, so "one app for all devices" is real rather than three
apps that happen to look similar for the first six months.

What is *not* shared is anything platform-specific, and the differences are deliberate:

| | Admin (web) | Resident (mobile) | Guard (tablet) |
| --- | --- | --- | --- |
| Refresh token | In memory only | Keychain / Keystore | Keychain / Keystore |
| Offline queue | None | None | Yes, persisted |
| Client platform string | `web` | `android` / `ios` | `guard` |
| Retirement schedule | Fastest | Standard | Slowest |

### Why the refresh token differs

Browser storage cannot be protected from script on the same origin. A refresh token there is a
long-lived credential one XSS away from theft, and rotation does not help — an attacker who
steals it simply uses it first. So the web build keeps it in memory and closing the tab signs
you out. That cost is acceptable for a console used at a desk in sessions; it would not be for
an app someone opens for ten seconds at a gate.

A phone can actually defend it. Keychain and the Android Keystore are hardware-backed on most
devices and unreadable by other apps, so the mobile builds persist it. The inconsistency is not
sloppiness — it is the two platforms' real security properties.

---

## The offline queue

The Guard app is the only one that queues, because a gate is the only place that cannot stop.
A guard on a tablet with a dead router still has vehicles arriving. The alternative to a queue
is the paper register, and a gate that falls back to paper tends to stay there — which is how a
society's digital gate log silently goes stale while everyone believes it is current.

Four properties make it safe rather than merely convenient:

**Every write goes through the queue, online or not.** Not "call the server, fall back on
failure". The behaviour is identical either way, so the offline path is exercised on every
single entry rather than being the rarely-run branch that turns out to be broken during the
outage it exists for.

**Idempotency keys are minted when the guard acts, not when the action is sent.** A request
that succeeded but whose response was lost is retried with the same key, and the server
recognises it. Without this, every flaky connection produces duplicate check-ins.

**Order is preserved, and a transient failure stops the drain.** A check-out replayed before
its check-in is rejected as a state violation and the entry is lost. Skipping past a stuck
action would send later ones out of sequence.

**A full queue refuses rather than dropping the oldest.** A guard who is told the queue is full
can act — call the office, use paper deliberately. A guard whose first entries silently
vanished has no idea anything is wrong.

Permanently rejected actions are *parked*, not discarded, and reported on screen. A rejected
entry means someone got through the gate with no record, and the guard is the only person still
able to do anything about it.

---

## Localisation

Resources live in `SocietyHub.Client.Shared/Localization` as `Strings.resx` (English) and
`Strings.hi-IN.resx` (Hindi), compiled into a strongly typed `Strings` class. Strongly typed
rather than runtime key lookup, because a key that no longer exists must be a build error — the
alternative fails as a blank label in production that nobody notices until a resident asks what
the empty button does.

Four tests guard the pair, and each exists for a failure that is otherwise invisible:

- Every English key has a Hindi translation. A missing one silently falls back to English, so a
  resident sees a screen half in each language and cannot tell whether the English part is
  untranslated or is saying something different.
- Hindi has no orphaned keys — one that exists only in Hindi was renamed in English and will
  never be shown to anyone.
- No value is empty. An empty string renders as a blank label rather than falling back.
- Placeholders match. A translation that drops its `{0}` throws at runtime, on a screen, in
  front of a resident — and only for people using that language, which is why it survives
  testing.

Resolution order is a stated choice, then the society default, then the device, then English.
A resident who picked Hindi keeps Hindi on a device set to English: the choice they made by hand
outranks a setting they may never have looked at.

The switcher labels each language in its own script — `हिन्दी`, not `Hindi`. Someone who cannot
read English cannot find "Hindi" in a list. That is the difference between a switcher that works
for the people who need it and one that only works for people who do not.

---

## The API client

Hand-written in `Api/SocietyHubApiClient.cs`, not generated, and that is a decision rather than
an omission.

Generation needs the six services running to produce their OpenAPI documents, which makes an
offline build impossible and a CI build dependent on a working database. The generated surface
would still need this layer wrapped around it for tokens, the client-version header,
refresh-on-401 and idempotency keys — and the typed methods are the only part generation would
have saved.

The generation path is kept viable rather than discarded. `scripts/generate-clients.sh` fetches
the live OpenAPI documents and can emit NSwag clients; run it after changing an endpoint and
compare. An empty diff means the hand-written client is still accurate; a non-empty one means it
is wrong, which is exactly what you want to find out.

Two behaviours worth knowing:

**Refresh happens once on a 401, then the session ends.** Not in a loop. A refresh that returns
a token the server rejects means something retrying cannot fix, and a client that keeps trying
turns one broken session into sustained load against the identity service — the service least
able to absorb it during an incident.

**Every non-GET carries an `Idempotency-Key`.** The transport retries on transient failures, and
without a key a flaky connection turns one pre-approved visitor into three.

---

## Building

The admin app and the shared library are in `SocietyHub.slnx` and build anywhere:

```bash
dotnet build SocietyHub.slnx
```

The two MAUI apps need workloads and are built per platform:

```bash
dotnet build src/Clients/SocietyHub.Resident.App -f net10.0-android
```

Two things in their project files are not cosmetic:

- `<TargetFramework></TargetFramework>` is cleared explicitly. `Directory.Build.props` sets a
  single framework for the whole repository — right for the six services, wrong here — and
  MSBuild gives the singular property precedence over the plural one. Without the clear, the
  project silently builds as plain `net10.0` and finds no MAUI target at all.
- `<ManagePackageVersionsCentrally>false</...>`. MAUI resolves its package versions from the
  installed workload through `$(MauiVersion)`, which central package management cannot express.
  Pinning it centrally means the repository disagreeing with the workload on disk, and that
  fails with errors pointing nowhere useful.

iOS and Mac Catalyst are opt-in via `-p:IncludeAppleTargets=true`, because they need a Mac build
host and leaving them in the default set makes the repository unbuildable on Windows and in CI
for a reason unrelated to the code. The app itself is platform-neutral Blazor and needs no
change to produce them.

The Guard app has no iOS target and is not meant to. It runs on a wall-mounted Android tablet a
society buys once and keeps for years; an iOS build would be an App Store listing, a review
cycle and a second retirement schedule for a device nobody uses at a gate.

---

## What is deliberately not built yet

Stated plainly rather than left to be discovered:

- **Camera and QR capture in the Guard app.** The gate screen takes a pass code by keyboard,
  which is the path that must never be removed — a code read aloud through a car window at
  night has to work. Camera capture is a faster path on top of that, not a replacement, and it
  needs a device to develop against.
- **Sign-in screens.** The token plumbing, refresh rotation and secure storage are complete and
  tested; the login forms that drive them are not built.
- **Push notification registration.** The server side exists (`/api/notification/push-tokens`);
  wiring Firebase and APNs into the mobile shells does not.

These are v1.0 scope and are tracked in Phase 4's launch-prerequisite group, not silently
dropped.
