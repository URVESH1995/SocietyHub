# Camera + AI feature catalogue

Everything the vision platform could offer a society, with an honest read on whether each
one actually works today.

## How to read the columns

**Maturity** is the important one, and it is where most vendor pitches are dishonest.

| Mark | Meaning |
| --- | --- |
| ✅ | Production-ready. Reliable enough to alert on without a human pre-filter. |
| ⚠️ | Works, but needs per-site tuning and will produce false positives. Alert a human, never automate an action. |
| ❌ | Demos well, fails in the field. Do not promise it to a committee. |

**Risk** is consent and privacy exposure: **None** (identifies nobody) · **Low** ·
**High** (biometric or identity-linked).

---

## 1. Gate and access control

| # | Feature | Maturity | Risk |
| --- | --- | --- | --- |
| 1 | **ANPR** — read plate, auto-classify resident vs visitor | ✅ | None |
| 2 | Auto-lift boom barrier for registered vehicles | ✅ | None |
| 3 | **Tailgating** — more people entered than the pass admitted | ✅ | None |
| 4 | Vehicle tailgating — second car through one barrier lift | ✅ | None |
| 5 | Auto-capture and auto-crop visitor photo at entry | ✅ | Low |
| 6 | Wrong-way detection — entry through the exit lane | ✅ | None |
| 7 | Guard post unattended — nobody at the gate | ✅ | Low |
| 8 | Helmet compliance for two-wheelers | ⚠️ | None |
| 9 | Resident face entry, opt-in only | ⚠️ | **High** |

Feature 1 is the sleeper. ANPR needs no consent negotiation, works at night, and answers
the question societies ask every single day — *whose car is in the visitor bay*.

Feature 3 is the best access-control signal available, and it identifies nobody. It only
counts.

---

## 2. Vehicle and parking

| # | Feature | Maturity | Risk |
| --- | --- | --- | --- |
| 10 | Parking slot occupancy — which bays are free | ✅ | None |
| 11 | Someone parked in another flat's allotted slot | ✅ | Low |
| 12 | Visitor parking overstay | ✅ | Low |
| 13 | **Fire lane blocked** — life-safety, and constantly violated | ✅ | None |
| 14 | In/out reconciliation — how many vehicles are still inside | ✅ | None |
| 15 | Abandoned vehicle — unmoved for N days | ✅ | Low |
| 16 | EV charging bay occupied by a non-EV | ✅ | Low |
| 17 | "Find my car" for residents | ✅ | Low |

---

## 3. Safety and emergency

Routed on the **SOS priority lane**. These must never queue behind a notice broadcast.

| # | Feature | Maturity | Risk |
| --- | --- | --- | --- |
| 18 | Fire and smoke detection in common areas | ✅ | None |
| 19 | **Pool drowning detection** | ⚠️ | Low |
| 20 | Child alone near the pool | ⚠️ | Low |
| 21 | Fall detection — elderly residents in lobbies and stairwells | ⚠️ | Low |
| 22 | Person motionless past a threshold | ⚠️ | Low |
| 23 | Lift entrapment — someone stuck inside | ✅ | Low |
| 24 | Basement water logging during monsoon | ✅ | None |
| 25 | Crowd surge or stampede conditions | ⚠️ | None |
| 26 | Violence and fight detection | ❌ | Low |
| 27 | Weapon detection | ❌ | Low |

**Feature 19 deserves its own line.** Pool drownings in Indian societies are a real and
recurring tragedy, and drowning is silent — nobody shouts. It needs proper overhead or
underwater camera placement to work, which is a genuine cost, but it is the single
highest-stakes thing on this entire list.

**Features 26 and 27 do not work.** Every CCTV vendor demos them; in the field they fire on
hugging, cricket, and umbrellas. Promising them to a committee is how you lose the account
when the first real incident is missed. Leave them out.

---

## 4. Perimeter and intrusion

| # | Feature | Maturity | Risk |
| --- | --- | --- | --- |
| 28 | Wall climbing — someone scaling the compound boundary | ✅ | Low |
| 29 | Virtual tripwire on a boundary line | ✅ | Low |
| 30 | After-hours access to restricted rooms — pump, DG, electrical | ✅ | Low |
| 31 | Terrace access outside permitted hours | ✅ | Low |
| 32 | **Loitering** near parked vehicles | ✅ | Low |
| 33 | Ladder or tool at the perimeter — a burglary precursor | ⚠️ | None |

---

## 5. Common areas and amenities

| # | Feature | Maturity | Risk |
| --- | --- | --- | --- |
| 34 | Amenity occupancy — how full is the gym right now | ✅ | Low |
| 35 | Amenity used without a booking | ✅ | Low |
| 36 | Unattended child in the play area | ⚠️ | Low |
| 37 | Pet off-leash in a restricted zone | ⚠️ | Low |
| 38 | Garbage bin overflow | ✅ | None |
| 39 | Illegal dumping of construction debris | ✅ | Low |
| 40 | Littering in common areas | ⚠️ | Low |

Feature 34 pairs neatly with amenity booking in the Phase 5 backlog — a live "gym is at 40%
capacity" figure is a small feature residents genuinely use every day.

---

## 6. Staff, vendors and service delivery

| # | Feature | Maturity | Risk |
| --- | --- | --- | --- |
| 41 | **Guard patrol verification** — did the round actually happen | ✅ | Low |
| 42 | Housekeeping verification — was the corridor actually cleaned | ⚠️ | Low |
| 43 | **Technician arrival and duration for a bulk drive** | ✅ | Low |
| 44 | PPE and uniform compliance for maintenance work | ⚠️ | Low |
| 45 | Domestic help attendance by face | ⚠️ | **High** |

**Feature 43 closes a real loop in Phase 2.** A bulk AC-servicing drive pays a vendor for
40 units. ANPR logs the van in and out; the gate camera timestamps the technicians. Now the
society can verify the vendor was actually on site for four hours and not forty minutes —
which is precisely the dispute that kills group-buying schemes.

**Feature 45 is the one I would refuse to build as designed.** Maids, cooks and drivers
cannot meaningfully consent to a biometric scan at a gate they must pass to earn a living,
and Indian housing societies have already faced disputes over exactly this. Use a QR or card
punch instead — it works, it costs nothing, and it carries none of the exposure.

---

## 7. Facility and operations

| # | Feature | Maturity | Risk |
| --- | --- | --- | --- |
| 46 | Lift usage analytics for maintenance planning | ✅ | None |
| 47 | Streetlight and common-area lighting faults | ✅ | None |
| 48 | Water tank overflow, visually detected | ✅ | None |
| 49 | Unauthorised flat renovation — debris and workers without a permit | ⚠️ | Low |
| 50 | Common-area cleanliness scoring over time | ⚠️ | None |

Features 46 to 48 auto-raise **Helpdesk complaints** rather than alerts. A streetlight fault
becomes a ticket with a photo attached, assigned before any resident has noticed.

---

## 8. Resident-facing convenience

| # | Feature | Maturity | Risk |
| --- | --- | --- | --- |
| 51 | Delivery left at gate — auto-notify with a photo | ✅ | Low |
| 52 | Visitor arrival notification with a photo | ✅ | Low |
| 53 | My vehicle's entry and exit history | ✅ | Low |
| 54 | My expected visitor has arrived | ✅ | Low |

These are the features residents actually notice. Everything else on this list is invisible
when it works.

---

## 9. Evidence, audit and reporting

| # | Feature | Maturity | Risk |
| --- | --- | --- | --- |
| 55 | Auto-bookmark a clip when an alert fires | ✅ | Low |
| 56 | Searchable incident timeline across all cameras | ✅ | Low |
| 57 | Audited clip export for police | ✅ | Low |
| 58 | Camera fleet health — a dead camera is worse than none | ✅ | None |
| 59 | Monthly security report for the committee | ✅ | None |
| 60 | Gate congestion and footfall analytics | ✅ | None |

Feature 58 matters more than it sounds. A camera that quietly stopped recording is worse
than no camera at all, because the society believes it is covered.

---

## Recommended build order

**Wave 1 — ship this first.** ANPR (1, 2), tailgating (3), fire lane (13), slot occupancy
(10, 11), loitering (32), restricted-zone intrusion (30, 31), camera health (58), delivery
and visitor notifications (51, 52).

All ✅ maturity, all zero or low consent risk, and every one of them produces something a
resident or guard sees the same day. This wave alone justifies the edge hardware.

**Wave 2 — life safety.** Fire and smoke (18), lift entrapment (23), water logging (24),
pool drowning (19), fall detection (21). Higher stakes, more tuning, needs careful camera
placement.

**Wave 3 — operations.** Patrol verification (41), technician verification (43), facility
faults (46–48), amenity occupancy (34), committee reporting (59, 60).

**Wave 4 — identity, only if the business requires it.** Resident opt-in face entry (9).
Nothing else from the High risk column.

---

## What this needs in hardware

| Zone | Camera type | Why |
| --- | --- | --- |
| Gate — vehicle lane | ANPR camera, IR, 1/1.8" sensor | Plate capture at speed and at night is a specialised job; a general camera will not do it |
| Gate — pedestrian | Standard 4MP dome | Tailgating, visitor capture |
| Perimeter | 4MP bullet with IR, 30m+ | Wall climbing, tripwire |
| Parking | 4MP dome, wide | Slot occupancy, fire lane |
| Lobby and lift | 4MP dome | Falls, entrapment |
| Pool | **Overhead or underwater** | Drowning detection does not work from a side-mounted camera |
| Common areas | Standard 4MP | General coverage |

Typical society: **16–24 cameras**, plus one edge box at roughly ₹25,000–60,000.

The ANPR camera and the pool camera are the two that cannot be substituted with a generic
unit. Everywhere else, standard hardware is fine.
