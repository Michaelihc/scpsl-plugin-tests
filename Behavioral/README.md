# SCP:SL Behavior Test Harness

This folder contains a private/headless LabAPI test plugin for behavior checks. Hint rendering stays in `.tests\UI`, and UI-producing behavior tests should capture HintServiceMeow (HSM) entries for that renderer.

When HSM is loaded, the harness snapshots the first live HSM `PlayerDisplay` with visible entries. During each scenario it records HSM-compatible JSON to `.tests\UI\output\captures\<scenario>` and renders captured snapshots through `.tests\UI\render-image.js`.

Build it:

```powershell
dotnet build .\BehaviorTestHarness.csproj
```

If the dedicated server is not installed at Steam's default path, point builds at the managed assemblies:

```powershell
$env:SCP_SL_MANAGED = "D:\path\to\SCP Secret Laboratory Dedicated Server\SCPSL_Data\Managed"
dotnet build .\BehaviorTestHarness.csproj
```

The project disables source-control metadata generation; if restore has already succeeded once, use:

```powershell
dotnet build .\BehaviorTestHarness.csproj --no-restore
```

Deploy the built DLL to the test server LabAPI plugin folder for the active port, then start a disposable headless server. For port `7777`, the plugin folder is:

```text
%APPDATA%\SCP Secret Laboratory\LabAPI\plugins\7777
```

Run behavior tests from the server console, query console, or Remote Admin:

```text
scptest run smoke
scptest run all
```

For UI-producing behavior scenarios:

- Keep assertions server-visible, as usual.
- Call the plugin feature normally through its HSM-backed provider path.
- At scenario end, the harness captures the current HSM entries and renders a 1080p PNG by default.
- For an explicit mid-scenario image, call `context.CaptureUiScreenshot("label")`.

Relevant config fields:

- `capture_hsm_ui`
- `render_hsm_ui_screenshots`
- `render_hsm_ui_on_manual_capture`
- `ui_harness_path`
- `ui_capture_output_directory`
- `ui_capture_viewport`
- `node_executable`

Portable UI capture setup:

- Install `.tests\UI` dependencies with `npm install` and `npx playwright install`.
- Set `SCPSL_UI_HARNESS_PATH` to the absolute `.tests\UI` path, or set `SCPSL_PLUGINS_METAREPO_ROOT` to the repo root and let the harness derive `.tests\UI`.
- Optionally set `SCPSL_UI_CAPTURE_OUTPUT_DIRECTORY`; otherwise captures go under `<ui_harness_path>\output\captures`.
- If these variables are not set, the config falls back to `.tests\UI` relative to the server process working directory and logs a clear `ui-harness-path-not-found` render error when that path is not valid.

The harness emits stable server log lines prefixed with `[BehaviorTest]`, for example:

```text
[BehaviorTest] dummy player spawned role=attacker player=BT-attacker-...#12
[BehaviorTest] dummy player attacked attacker=BT-attacker-...#12 target=BT-target-...#13 requestedDamage=15 health=100->85 applied=True
[BehaviorTest] dummy exp increased player=BT-attacker-...#12 amount=25 exp=0->25
```

Available commands:

- `scptest run <scenario>` runs one scenario. Defaults to `smoke`.
- `scptest run all` runs every discovered scenario.
- `scptest list`
- `scptest reload` rediscovers scenarios from loaded assemblies.
- `scptest cleanup`

The current `smoke` scenario validates the harness path itself: spawn dummies, assign human roles, position them, add an item, apply attributed damage, and increment a harness-local XP ledger. Plugin-specific assertions should build on this runner rather than touching real SCP:SL clients.

## Writing Scenarios

Agents should add behavior tests as `IBehaviorScenario` implementations. The simplest path is:

1. Copy `Scenarios/ScenarioTemplate.cs.txt` to `Scenarios/<FeatureName>Scenario.cs`.
2. Give it a unique `Name`.
3. Arrange dummies and plugin state in `Run`.
4. Act through LabAPI/server APIs, plugin commands, or dummy actions.
5. Assert with `context.Require(...)`.
6. Log important observations with `context.Info(...)`.
7. Build, deploy, run `scptest list`, then `scptest run <name>`.

Scenarios are discovered from all currently loaded assemblies. That means plugin-specific tests can also live in a separate test assembly that references `BehaviorTestHarness.dll`; drop that assembly into the private test plugin environment and run `scptest reload`.

Good dummy behavior tests:

- role assignment, custom role state, cooldowns, charges, and server-side timers
- damage, healing, shields, reward/progression changes, and event ordering
- inventory mutations, pickups, command behavior, permissions, and cleanup
- plugin state that can be observed from server logs, wrappers, fields, or public test hooks

Prefer RA/dummy actions for meaningful gameplay behavior. Unless the test is trivial, isolated, or explicitly validating a low-level server API, arrange the world with server APIs, then drive the behavior through the same dummy action path an admin would use:

- spawn the dummy
- assign role, inventory, and position
- face the target or object
- trigger the relevant RA dummy action, such as a movement, jump, interact, attack, or item action
- assert the resulting server-visible state

For example, a weapon/plugin behavior test should normally make the dummy equip the weapon, face the target, and trigger the dummy shoot/action path instead of calling `target.Damage(...)` directly. Direct damage is fine for the smoke scenario and narrow harness plumbing tests, but it skips too much real behavior for most plugin scenarios.

Keep out of this harness:

- direct client-side layout assertions; capture HSM entries and let `.tests\UI` render/check them
- real client automation, modified clients, anti-cheat bypasses, or IL2CPP patching
- assertions that depend on a human player looking at UI
- flaky wall-clock waits when an event or explicit state check would work

Use stable, grep-friendly log lines. Prefer:

```text
[BehaviorTest] dummy player attacked attacker=... target=... health=100->85 applied=True
```

over prose that changes from run to run.
