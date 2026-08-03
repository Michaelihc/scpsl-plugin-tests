# PlaytestHarness (v2) — phases A–C

Coroutine-native, tiered playtest harness for SCP:SL LabAPI plugins. Greenfield replacement track
for `.tests\Behavioral` (which stays untouched/legacy). Plan of record:
`.tests\docs\part1-harness-v2-plan.html`.

**Status: phases A+B+C complete.** Runner + catalog + telemetry + `ptest` command + auto-run (A);
actor API (`ctx.SpawnActor` + verbs), placement probes, fast-travel, native action verbs (LookAt /
GiveItem / Equip / UseHeldItem incl. the Micro H.I.D. real-charge adapter / Attack with real
ballistics + native reload readiness), pre-arranged-state scope (`ctx.Arrange`), MonitorHost +
BoundsMonitor + TeleportMonitor (3D, one-shot ordered-move marks) + role watchdog, monitor
violations fail the offending scenario (B); route walking via the reflection-bound `IMovementProvider`
seam (`BotOrdersProvider` -> spike `SCPSLBot.AI.BotOrders`, incl. lifecycle `Release`), direct native
speed trials via stepped `FpcMotor.ReceivedPosition`, `WalkTo`, EndToEnd rules (`SpawnSpec.At`/`Damage()`/`GiveItem`/`Arrange` throw, `GoTo` walks), SKIP on
provider-less ports, concurrent verbs (C). Pilot migration (D) and docs/adoption (E) pending.

## Actor API in one glance

```csharp
Actor a = ctx.SpawnActor("walker", RoleTypeId.ClassD, SpawnSpec.Native());
yield return a.WaitReady();                  // role + placement + settle
yield return a.GoTo(RoomName.LczToilets);    // quick: TP · standard: fast-travel · e2e: walk
yield return a.WalkTo(target);               // walked at standard+; provider absent => SKIP
ctx.Arrange("slice starts armed",            // standard's 2nd shortcut: pre-arranged state
    () => a.GiveItem(ItemType.GunCOM15));    //   (quick: free; e2e: Arrange itself throws)
yield return a.Equip(ItemType.GunCOM15);     // native selection + draw
yield return a.Attack(victim);               // native ready+aim+fire, hit-confirmed, retry w/ timeout
yield return a.UseHeldItem();                // native lifecycle — Micro H.I.D. really charges
yield return a.Soak(30f);                    // leave alone; bounds+ground monitored

Actor speedProbe = ctx.SpawnActor("speed", RoleTypeId.Scp939, SpawnSpec.Native(),
    useMovementProvider: false);              // plain RA dummy; no bot movement patch
yield return speedProbe.WaitReady();
yield return speedProbe.MeasureWalkingSpeed(Vector3.forward, 1f);
MovementMeasurement measured = speedProbe.LastMovementMeasurement!;
// measured.InputPath == "fpc-received-position": nearby steps through the real FpcMotor.

// Concurrency: register several verbs, then yield once — they run in parallel:
a.WalkTo(x); b.WalkTo(y); yield return ctx.WaitOneFrame();
```

BATTERIES-INCLUDED CONTRACT: aim/rotation/raycast/weapon-state/charge-wait mechanics live inside
verbs. A scenario containing Quaternion/aim math is a defect — add a harness verb (or a
CombatFulfiller item adapter) instead.

## Fidelity tiers — STRICT semantics

- `quick` — regression sweep; shortcuts legal (raw TP, `Damage()`, free `GiveItem`).
- `standard` (default) — the automated manual playtest of a *slice*. **Identical gameplay rules to
  e2e**; exactly two shortcuts: ① **fast-travel** — `GoTo` probe-validates the target (ground
  within drop, in-bounds, capsule clearance) *before* the TP, settles after, and FAILS loudly on a
  bad target; ② **pre-arranged state** — `ctx.Arrange("reason", ...)` scopes, telemetry-logs and
  gates `GiveItem`. Everything else is native at native speed: a 20 s charge takes 20 s, damage
  only from real dummy shots (`Damage()` throws).
- `e2e` — the whole path, zero shortcuts: native role spawns only (`SpawnSpec.At` throws), all
  travel walked via the movement provider (`GoTo`≡`WalkTo`; ANY teleport is a TeleportMonitor
  violation), no `Arrange`, no `GiveItem`, no `Damage()`.

## Monitors

`BoundsMonitor` (out-of-every-room, sustained airborne, sinking), `TeleportMonitor` (3D per-tick
displacement budgets; harness-ordered moves consume one-shot marks; at e2e any TP is a violation),
plus the registry role watchdog (external role set on an owned actor — the mark is consumed after
spawn, so even a same-role external set is flagged). Violations land in the JSONL stream, the run
summary, AND fail the scenario they occurred in. `ctx.Monitors` is a separate read-only facade whose
runtime type has no monitor lifecycle/control APIs; every query returns an immutable snapshot. The
live `MonitorHost` remains harness-internal. Self-tests may waive only an exact monitor + actor +
count + time-window match through `ctx.ExpectViolation(...)`; extra or unmatched violations fail.

## Movement provider seam

`Movement\IMovementProvider` is harness-owned; `BotOrdersProvider` binds `SCPSLBot.AI.BotOrders`
via reflection at first use (zero compile-time reference — the planned bot rewrite ships a new
provider, harness and scenarios unchanged). Provider absent ⇒ walk-requiring scenarios SKIP with an
explicit reason; fast-travel/standard scenarios still run. Stall/off-mesh/no-path ⇒ loud FAIL with
the provider's structured reason, never a hang.

## Build

```powershell
dotnet build .tests\Playtest\PlaytestHarness.csproj -c Release
```

net48; references resolve from `$(SCP_SL_MANAGED)` (default: the local Steam dedicated-server
Managed folder). Repo-root `Deploy.targets` refreshes existing deployed copies only — the first
deploy to a port is a manual copy of `bin\Release\PlaytestHarness.dll` into
`%APPDATA%\SCP Secret Laboratory\LabAPI\plugins\<port>`. Server restart required after every dll change.

## Run

From Remote Admin or the game console:

```text
ptest run <name|suite|all> [quick|standard|e2e]   # default standard
ptest list | reload | status | cleanup | report   # reload re-discovers scenarios (idle only)
```

Built-in scenarios: `smoke` (quick), `placement-soak` (quick..standard), `demo-fast-travel`
(standard), `demo-combat` (quick..standard), `native-charged-item` (standard), `demo-walk`
(standard..e2e). By-name-only demos (excluded from `run all`): `bad-goto-contrast` (tier-contrast
proof), `walk-offmesh-demo` (deliberate loud-fail), `monitor-selftest` (quick),
`monitor-e2e-check` (e2e adversarial TP catch).

Grep-stable log lines: `[Playtest] SCENARIO/STEP/PROBE/RESULT/VIOLATION/RUN_RESULT ...`; a run ends with

```text
[Playtest] RUN_RESULT level=<x> passed=N failed=N skipped=N summary=<path>
```

Artifacts land in `%APPDATA%\SCP Secret Laboratory\LabAPI\configs\<port>\PlaytestHarness\runs\`:
one `<timestamp>-<level>.jsonl` event stream + `<timestamp>-<level>.summary.json` per run.

Headless auto-run: set `auto_run_scenario` / `auto_run_level` / `auto_run_delay_seconds` in the
per-port `config.yml`; the run fires after server-ready and emits the same `RUN_RESULT` line. Idle
mode is paused while a run is active (empty-server MEC stall trap).

Config knobs (per-port `config.yml`): `teleport_threshold_meters`, `fast_travel_max_drop_meters`,
`walk_timeout_base_seconds`/`_per_meter`/`_max_seconds`, `settle_max_drop_meters`,
`settle_timeout_seconds`, `soak_sample_interval_seconds`, `angular_tolerance_degrees`,
`attack_timeout_seconds`, `monitor_sample_interval_seconds`.

## Writing scenarios

Start from `Scenarios\ScenarioTemplate.cs.txt`. Rules that matter:

- PASS = the coroutine ran to completion. `Require` failure = FAIL, other exception = ERROR,
  timeout = FAIL (killed), monitor violation during the scenario = FAIL. No early `Pass()` exists.
- Yield real waits (`ctx.Wait`, `ctx.WaitUntil`).
- Batteries included: scenarios call harness verbs; never inline aim/align/quaternion mechanics.
  Missing verb/item adapter => add it to the harness first.
- Declare an honest `Supported` range; cheating scenarios are Quick-only.
- Use `Suites` for an intentional multi-scenario battery. Suite names may be shared; names and aliases remain unique.

This README will be expanded in phase E.
