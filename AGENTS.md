# Test Instructions

## Run UI Tests

Use UI tests for HSM/hint layout, generated screenshots, and renderer regressions.

```powershell
cd .\.tests\UI
npm install
npx playwright install
node smoke-test.js
node render-image.js --fixture "HSM Calibration Rectangle" --output output/playwright/hsm-calibration-harness.png --viewport 1920x1080
```

Interpretation:

- `static_issues=0` means the fixtures parsed cleanly.
- Generated PNGs are expected under `.tests\UI\output`.
- Default screenshot comparison target is 1080p: `1920x1080`.
- If UI output changes, include the generated screenshot path in the test transcript or final response.
- `node_modules` is local setup. Do not commit it.
- For SCP:SL hint layout/collision work, render against the real native screenshot backgrounds registered in `.tests\UI\backgrounds.js` before making or relying on synthetic native UI. Prefer highlighted backgrounds such as `waiting-for-players-highlighted`, `announcements-and-stat-bar-highlighted`, `inventory-highlighted`, `scp-106-minimap-highlighted`, `scp-abilities-highlighted`, `spawn-flash-highlighted`, or `spectator-highlighted` when checking native UI collisions.

### HSM hint positioning (READ before placing hints)

The `.tests\UI` harness ENCODES + WARNS on the in-game-verified HSM traps — the full list is the "HSM RENDERING" header in `.tests\UI\preview-core.js` (and `README.md`). Essentials: Y is 1:1 and centre-X is linear+symmetric (~0.556 px/HSM-unit, X=0 = centre — trust the harness for these); reach a screen edge ONLY via **Center + large X** (≈±1745; Left/Right alignment is for flush-justified blocks — it clamps ~px1620 right / ~px76 left and can't reach the literal edge, so don't "fix" that in the HSM fork — tried+reverted); the text area is asymmetric (**wrap-safe ≈ px76..1620**) and wide text past ~px1620 word-wraps (`<nobr>` won't save it — keep text in-band or use single-token edge markers); `HintVerticalAnchor.Bottom` is off-screen (Middle/Top only). Confirm left/right-edge work in-game. Memory: `hsm-coordinate-calibration`.

## Run Behavior Tests

Use behavior tests for server-visible plugin effects: roles, health, inventory, effects, cooldown state, rewards, commands, emitted logs, and whether relevant UI triggers fired.

Run scenarios from the server console, query console, or Remote Admin:

```text
scptest list
scptest run smoke
scptest run all
```

Interpretation:

- Behavior scenario logs should use the `[BehaviorTest]` prefix.
- A passing scenario must call `context.Pass()` only after the behavior is actually observed.
- Prefer deterministic assertions over sleeps.
- UI screenshots produced during behavior runs are evidence for hint output, not a replacement for server-visible assertions.

## Writing Tests

- Plugin-specific tests should be self-contained in that plugin's own project folder, usually under a local `tests` folder.
- Use `.tests` as the shared harness/tooling area, not as the long-term home for every plugin's private test cases.
- Add behavior scenarios as `IBehaviorScenario` classes.
- Start from `.tests\Behavioral\Scenarios\ScenarioTemplate.cs.txt`.
- Give each scenario a unique name.
- Assert with `context.Require`.
- Return `context.Pass()` only after the behavior is actually observed.
- Keep scenarios deterministic: assign roles explicitly, set positions explicitly, avoid live-player dependencies, clean up dummies, and prefer event/state assertions over arbitrary sleeps.
- Keep rendering checks in `.tests\UI`; behavior scenarios may trigger hint capture, but should not implement their own renderer.
- Verify that spawned roles and objects do not fall through the map. If they do, correct their coordinates/offsets, ideally using game-provided room positions.
- Prefer RA/dummy actions over direct effects for meaningful gameplay behavior, like spawning a dummy to shoot at a target instead of just calling `Player.Damage(...)`.
- Direct calls such as `Player.Damage(...)` are acceptable for setup, cleanup, smoke plumbing, or deliberately isolated low-level checks.

## Think Like A Player — In-Game Probing (REQUIRED)

Server-visible state assertions are not enough. Before calling any spatial/gameplay feature done — and in
**all** adversarial reviews — you MUST probe it in-game with server dummies and raycasts. Gravity DOES act
on dummies (a bit wonky, but real), so dummies are a valid way to catch falls, gaps, and bad spawns.

Probe like a real player would experience it:

- **Look around with raycasts — lots of them.** Cast down from spawn/teleport points to confirm there is
  solid ground within a sane drop (and that the player is not inside or under geometry). Cast forward/around
  to confirm walls, pedestals, and props are where you placed them and are actually collidable. Raycasts are
  cheap and this harness is NEVER deployed, so do not worry about raycast perf — cast generously.
- **Walk it.** Spawn a dummy at the real spawn point, let physics settle a few frames, and assert it did not
  fall through the floor or out of the room (check Y stayed sane / it is still near the intended position).
  Nudge the dummy around the floor and into walls to confirm collision holds.
- **Spam abilities and inputs.** Spawn the relevant roles/SCPs and fire their abilities repeatedly and out of
  order (e.g., SCP-106 stalk/teleport, 173 blink, 939 lunge, grenades, item use) at and around the feature.
  Try it during the wrong phase, on the wrong target, twice in a row, mid-teleport — try to break it.
- **Try to break things generally.** Join/leave mid-flow, reconnect, change role at a bad moment, stand where
  you should not, shoot/melee the props, overflow inputs. Adversarial review means actively attacking the
  feature in-game, not just reading the diff.

Practical notes:

- Collidable AdminToy primitives are walkable, but verify it — confirm `PrimitiveFlags.Collidable` is set AND
  that a downward raycast from the spawn point actually hits the toy's collider before trusting players to
  stand on it. Floating-far-from-origin rooms are exactly where this silently fails.
- If a real client connection is unavailable, raycasts + dummies are the minimum bar — never ship a spatial
  feature with zero in-game probing.
- Prefer game-provided room/door positions for anchoring over hardcoded world coordinates; if you must float a
  room, raycast-verify the floor catches a settling dummy.

## Test Transcript

When reporting test work:

- Provide a transcript for new features.
- Summarize old regression coverage as `x/x tests passed`.
- Include the generated UI screenshot path when UI output is involved.
- Keep log output stable and grep-friendly; behavior logs should use the `[BehaviorTest]` prefix.

## Live Verification

For plugin code changes or QA that need live/manual verification, deploy to:

```text
%APPDATA%\SCP Secret Laboratory\LabAPI\plugins\<active port>
```

**ALWAYS grant the user owner when setting up a NEW local port** — a freshly generated
`config_remoteadmin.txt` only contains the `SomeSteamId64@steam: owner` template, so the user
connects with no Remote Admin at all. After the first boot generates the config, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .references\grant-owner.ps1 -Port <port>
```

then RESTART that port's server (a port with no authorized RA user cannot run `pm reload`). When
killing a local server, kill `LocalAdmin.exe` BEFORE its `SCPSL.exe` child — LocalAdmin auto-respawns
the game process, and an orphaned pair silently keeps the UDP port bound while a new boot appears to
succeed.

Then start/restart the visible test server. Do not launch the server headless for release/manual QA.

### Headless per-port playtesting (automated loops / subagents)

For automated dummy playtest loops (NOT release QA), boot headless servers on spare ports with
`.references\run-port.ps1 -Port <n>` (parameterized; isolates `la<port>.cmd/.log/.pid`). Give EACH
parallel server / subagent its OWN port (7905, 7906, 7908, …) — they collide on shared command/log
files otherwise. Stop a server with `Stop-Process -Id (Get-Content la<port>.pid)` plus its child
`SCPSL.exe` (do not mix `taskkill /PID` and `Remove-Item` in one PowerShell call — the sandbox flags it).
Auto-running verifiers (reinforcements `BehaviorScenarios` Plugin, scp-096 `Scp096LevelsLiveTest`) fire
~32s after load; grep `la<port>.log`. Gotchas learned the hard way:

- **DummyRoleFiller** force-re-rolls dummies to ClassD (SCP dummy reverts: `MaxHealth`=100, role cast null).
  Remove `DummyRoleFiller.dll` from the test port, or use a port without it, when spawning SCP dummies.
- Dummies do NOT inherit a role's `MaxHealth` / Hume-Shield-curve values — set `MaxHealth`/`HumeShield`
  manually for meaningful checks. For an HS sticky-override check, reflect `HumeShieldStat._maxValueOverride`.
- `Player.HumeShieldRegenRate` GETTER is cooldown-gated (effective; often 0); the SETTER writes the
  configured rate. Read `((DynamicHumeShieldController)role.HumeShieldModule).RegenerationRate` for a true baseline.
- Deploy from the SAME build config you built (Debug-vs-Release mismatch silently ships a STALE dll);
  `Deploy.targets` only refreshes EXISTING copies; net48 needs a server RESTART; run ONE auto-harness per port.

## Additional Details

- UI rendering tests live in `.tests\UI`. See `.tests\UI\README.md`.
- Behavior tests live in `.tests\Behavioral`. See `.tests\Behavioral\README.md`.
