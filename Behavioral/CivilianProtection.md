# CivilianProtection behavior tests

These tests require `CivilianProtection.dll`, `BehaviorTestHarness.dll`, and optionally
`HintServiceMeow.dll` on the same native LabAPI test port. Run them only after the round is active;
SCP:SL rejects ordinary player damage during Waiting for Players.

## Automated server assertions

From LocalAdmin, the server console, or Remote Admin:

```text
scptest run civilian-protection
```

Expected result: `Behavior scenario 'civilian-protection' passed.`

The scenario uses four owned dummies and checks the real `PlayerEvents.Hurting` path:

- Foundation damage to an empty-inventory Class-D is blocked.
- A medkit does not count as a weapon, so protection remains.
- A firearm makes the Class-D armed, so Foundation damage applies.
- Chaos damage to an empty-inventory Scientist is blocked.
- A grenade makes the Scientist armed, so Chaos damage applies.
- Scientist/Class-D damage remains blocked in both directions even while both carry weapons.

The harness cleans its dummies after the run. Evidence is logged with the `[BehaviorTest]` prefix.

## Interactive client/HSM test

This test must be run by a connected player from in-game Remote Admin. It temporarily changes the
participant to Class-D and replaces their inventory. Finishing or cancelling restores the prior role
and position with that role's normal loadout; an exact custom inventory is not preserved. The command
also holds the round lock during its two short stages and restores the prior lock state during cleanup.

1. Join port `7777`, wait until the round is active, and open Remote Admin.
2. In RA CLI, run:

   ```text
   civprotest start
   ```

   The server spawns a Facility Guard dummy and applies 15 test damage. Health must remain unchanged.
   Confirm the green `PROTECTED` / `已受保护` HSM hint is visible.

3. Run:

   ```text
   civprotest armed
   ```

   The test adds a COM-15, verifies that the red `PROTECTION LOST` / `保护已失效` hint is triggered,
   and applies 15 damage again. Health must decrease.

4. Record what you actually saw:

   ```text
   civprotest finish pass
   ```

   If either hint was missing or incorrect, use `civprotest finish fail`. Use `civprotest cancel` at
   any stage to clean up and restore the prior role/position.

The final command logs the participant's visual result with the `[BehaviorTest]` prefix so a server-side
health assertion cannot be mistaken for an on-client UI pass.
