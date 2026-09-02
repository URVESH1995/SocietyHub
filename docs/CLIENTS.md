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

## Sign-in

Phone and a one-time code, no password anywhere. `SignInForm` is shared, so the console, the
resident app and the guard tablet cannot drift on OTP handling, error wording or the
multi-society choice — the last of which is the case most easily got wrong and least often
exercised.

That choice is real: a phone can be a Guard at one address and on a committee at another, so
verifying a code returns *either* tokens *or* a list of societies on the same 200. The client
parses on the discriminating field rather than deserialising into one type and hoping.

Error codes are mapped from the ones Identity actually emits (`Otp.TooManyRequests`, not
`otp.rate_limited`), and a test reads both sides and fails if either renames one. That test
exists because the mapping was written from memory the first time and every code was wrong —
a rate-limited resident saw "something went wrong" instead of being told to wait, and nothing
failed or logged.

**In development the code is returned on the wire and shown on screen**, because a local run
has no telecom account behind it. A production build never populates that field, and the
on-screen notice is deliberately styled to look like something that must not ship.

---

## Push notifications

Push carries almost everything the platform sends, because SMS is reserved for emergencies on
cost grounds. That makes a stale token an expensive kind of silence: the server sends happily
to an address that no longer exists, nothing errors, and a resident simply stops hearing about
visitors at their gate.

`PushRegistrationService` owns the lifecycle and is fully covered by tests, because the
lifecycle is where the failures are:

- Registration happens **after sign-in**, not at start-up — the endpoint is society-scoped.
- An unchanged token is not re-sent. Across 42,000 flats, every launch would otherwise be a
  database write for data that did not change.
- A **rotation** re-registers without a restart. The platform rotates on reinstall and on
  restore from backup; an app that registers once at install goes silently unreachable months
  later and nobody connects the two events.
- A **failed registration is not cached**, so it retries. Caching before the server confirms
  leaves a device permanently unreachable while appearing registered.
- Registration failing **never throws**. A resident with no notifications still needs to open
  the app.

### Configuration you must supply

The Android provider is real — `FirebaseDeviceTokenProvider` uses Firebase Cloud Messaging —
but Firebase needs a project, and the project is yours. It carries your sender id and keys and
nobody else's will do:

1. Create an Android app in the Firebase console using the id from the csproj
   (`com.companyname.societyhub.resident.app`).
2. Put `google-services.json` in `Platforms/Android/`.
3. Reference it: `<GoogleServicesJson Include="Platforms\Android\google-services.json" />`.
4. Give the Notification service the FCM server key, so it can send to the tokens this
   registers.

**Without that file the app still builds, runs, signs in and works** — `GetTokenAsync` returns
null and the registration service treats it as "no push available", which is the same path a
device takes when notification permission is refused. Crashing on a missing config file would
make the app undevelopable for anyone without your credentials.

iOS needs the APNs equivalent and a Mac to build; the provider seam is the same.

---

## Camera and QR

The guard tablet scans a pass with `ZXing.Net.Maui`, behind an `IBarcodeScanner` seam so the
gate screen stays testable without a camera.

**Scan is beside the typed field, never instead of it.** A code read aloud through a car window
at night, or a pass on a cracked phone screen behind glass, has to work. The keyboard path is
primary and the camera is the faster option on top of it — and cancelling a scan silently
returns to the keyboard rather than reporting anything.

Details that are not incidental:

- The scanner is **modal, not embedded**. A live preview on the gate screen holds the camera
  open all shift, drains a wall-mounted tablet on a marginal charger, and points a lens at the
  gate recording nothing — which a resident committee will eventually ask about.
- **Camera permission is requested at the point of use**, not during onboarding. A prompt shown
  before anyone has seen why it is needed is the one most often refused, and a refused
  permission on a wall-mounted tablet is awkward to recover from without physical access.
- `android.hardware.camera` is declared `required="false"`. Without that, Google Play hides the
  app from every device with no camera — including some of the cheap tablets societies actually
  buy. The typed path works on all of them, so the app must stay installable.
- Only QR and Code 128 are decoded. Every extra format is work on every frame, and on low-end
  hardware that is the difference between an instant read and holding a phone steady for five
  seconds.
- Detection **detaches on the first result**. The camera keeps producing frames while the modal
  dismisses, and a second detection could check the same visitor in twice.

---

## What has not been verified on a device

Stated plainly. Both MAUI apps **build for Android** and their logic is unit-tested, but no
screen has been opened on real hardware:

- The camera scan path needs a physical tablet — an emulator's simulated camera does not
  exercise autofocus, low light, or a pass held behind glass.
- Push delivery needs a Firebase project and a real device; an emulator without Play Services
  never issues a token.
- Keychain and Keystore behaviour after an OS upgrade — the case
  `MauiSecureStorage.GetAsync` catches and treats as "signed out" — only reproduces on a
  device that has actually been upgraded.

Everything around those three — token lifecycle, offline queue, sign-in, localisation — is
covered by tests that run without hardware.
