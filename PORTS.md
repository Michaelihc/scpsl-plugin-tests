# Local SCP:SL Test Port Registry

This is the central registry for local SCP:SL test ports shared by this metarepo. Check it before claiming a port, then verify the live process/socket state because multiple development sessions can run concurrently.

Last inventory refresh: 2026-08-11.

## Rules

- `7778` is EXILED-only. Never deploy native LabAPI plugins there.
- Ports containing `ReinforcementsSystem.dll` are auto-deploy targets and must not be used for isolated debug builds.
- A port is considered provisioned when it has either a dedicated-server config directory or a LabAPI plugin directory.
- Before reusing a port, check its plugin/config directories, `SCPSL.exe` and `LocalAdmin.exe` command lines, and bound UDP sockets.
- New ports must be added here, booted once, granted the local owner account with `.references\grant-owner.ps1`, and restarted.
- Automated headless tests may use `.references\run-port.ps1`; final/manual release QA uses a visible LocalAdmin server.

## Provisioned and reserved ports

| Port | Purpose / installed plugin set | Constraints |
|---:|---|---|
| 7777 | Main native integration stack; BehaviorTestHarness, PlaytestHarness, HSM, Reinforcements, StaffSuite, toys and production-like plugins | Reinforcements auto-deploy target; crowded |
| 7778 | EXILED loader only | EXILED-only |
| 7779 | StatsSystem/MySQL test port | Existing config and plugins |
| 7799 | Warden and Warden.Tickets | Existing product port |
| 7901 | Large native integration stack | Reinforcements auto-deploy target; crowded |
| 7902 | CodexStressTest + DummyRoleFiller | DummyRoleFiller changes roles |
| 7903 | HSM load testing | HSM-specific |
| 7905 | CodexStressTest + DummyRoleFiller | DummyRoleFiller changes roles |
| 7906 | Reinforcements behavior/integration stack | Reinforcements auto-deploy target |
| 7907 | SCPEnhancements | Product-specific |
| 7908 | Reinforcements behavior tests + HSM | Reinforcements auto-deploy target |
| 7909 | GOC Nuke behavior tests | Product-specific |
| 7910 | SCPSLBot infrastructure | Bot test port |
| 7911 | PlaytestHarness + SCPSLBot | Shared playtest port |
| 7912 | Reinforcements playtest + SCPSLBot | Product-specific |
| 7913 | MultiserverBan + Reinforcements behavior tests | Reinforcements auto-deploy target |
| 7914 | MultiserverBan + Reinforcements behavior tests | Reinforcements auto-deploy target |
| 7915 | Reinforcements behavior/integration stack | Reinforcements auto-deploy target |
| 7916 | Reinforcements behavior/integration stack | Reinforcements auto-deploy target |
| 7921 | Reinforcements behavior tests | Reinforcements auto-deploy target |
| 7922 | Reinforcements behavior tests + HSM | Reinforcements auto-deploy target |
| 7923 | Large production-like native stack | Reinforcements auto-deploy target; crowded |
| 7924 | Reinforcements behavior tests + HSM | Reinforcements auto-deploy target |
| 7926 | Reinforcements behavior tests | Reinforcements auto-deploy target |
| 7933–7941 | Reinforcements behavior-test matrix | Reinforcements auto-deploy targets |
| 7942 | CyberpunkCity/SCP999/ScpTiers/ProjectMER/HSM integration | No longer clean; Reinforcements present |
| 7943 | ProjectMER/SCP999/ScpTiers/HSM integration | Reinforcements present |
| 7951 | Large production-like native stack | Reinforcements auto-deploy target; crowded |
| 7952 | AutoEvent/RedGreenLight/VoiceCull test stack | Existing product port |
| 7960 | SCPEnhancements + HSM + spatial markers | Product-specific |
| 7961 | Warmup range verifier | Product-specific |
| 7962–7964 | CivilianProtection playtest matrix | Product-specific |
| 7965 | SpinBot | Product-specific |
| 7970 | CivilianProtection + HSM | Product-specific |
| 7977 | CyberpunkCity bounds verification | Product-specific |
| 7988 | Warmup range verifier + HSM | Product-specific |
| 7997 | Warmup range verifier + HSM | Product-specific |
| 7998 | WarmupScpSelector + HSM | Product-specific |
| 7999 | Reinforcements behavior tests, HSM and ToyTricksDemo | Reinforcements auto-deploy target; no longer clean |
| 8001 | Plugin audit batch: GlassCannon, UIU, UIUEntryAnimation and InvincibleWarMark | Reserved 2026-08-11 for isolated audit/fix verification |
| 8888 | DynamicMapLayout behavior tests + MultiserverBan | Product-specific |

## Live refresh commands

```powershell
$sl = Join-Path $env:APPDATA 'SCP Secret Laboratory'
Get-ChildItem (Join-Path $sl 'config') -Directory | Select-Object -ExpandProperty Name
Get-ChildItem (Join-Path $sl 'LabAPI\plugins') -Directory | Select-Object -ExpandProperty Name
Get-CimInstance Win32_Process |
  Where-Object Name -in 'SCPSL.exe','LocalAdmin.exe' |
  Select-Object ProcessId, ParentProcessId, Name, CommandLine
Get-NetUDPEndpoint |
  Where-Object LocalPort -ge 7777 |
  Sort-Object LocalPort |
  Select-Object LocalAddress, LocalPort, OwningProcess
```

The live inventory is authoritative when it differs from this document. Update this file when a port is created, repurposed, or retired.
