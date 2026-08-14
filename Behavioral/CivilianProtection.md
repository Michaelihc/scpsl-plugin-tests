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
- A firearm makes the Class-D armed, so Foundation damage applies; removing it restores protection.
- Chaos damage to an empty-inventory Scientist is blocked.
- A grenade makes the Scientist armed, so Chaos damage applies; removing it restores protection.
- Scientist/Class-D damage remains blocked in both directions even while both carry weapons.

The harness cleans its dummies after the run. Evidence is logged with the `[BehaviorTest]` prefix.

## Interactive live-fire client/HSM test

This test must be run by a connected player from in-game Remote Admin. It temporarily changes the
participant to Class-D and replaces their inventory. Finishing or cancelling restores the prior role
and position with that role's normal loadout; an exact custom inventory is not preserved. The command
also holds the round lock during its two short stages and restores the prior lock state during cleanup.

1. Join port `7777`, wait until the round is active, and open Remote Admin.
2. In RA CLI, run:

   ```text
   civprotest start
   ```

   You become a Guard with an equipped COM-15. Close RA and shoot both named Class-D dummies once:
   the left empty-inventory target must be protected, while the right target visibly holding a gun must
   take damage. The harness records both real shot events, so missing either target cannot pass.

3. Run:

   ```text
   civprotest check
   ```

   On pass, you become a Scientist with an equipped COM-15. Close RA and shoot the named armed Class-D
   target once; Scientist→Class-D damage must be blocked regardless of weapons.

4. Run:

   ```text
   civprotest check
   ```

   On pass, you become a Class-D with an equipped COM-15. Close RA and shoot the named armed Scientist
   target once; Class-D→Scientist damage must also be blocked.

5. Run `civprotest check` once more, then record whether the amber blocked hints appeared during the
   three protected shot phases:

   ```text
   civprotest finish pass
   ```

   If either hint was missing or incorrect, use `civprotest finish fail`. Use `civprotest cancel` at
   any stage to clean up and restore the prior role/position.

The final command logs the participant's visual result with the `[BehaviorTest]` prefix so a server-side
health assertion cannot be mistaken for an on-client UI pass.
