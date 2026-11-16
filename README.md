<p align="center">
  <img src="docs/cover.jpg" alt="Bannerlord Together Crash Guard" width="900">
</p>

<h1 align="center">Bannerlord Together Crash Guard</h1>

<p align="center">
  A compatibility patch for the <strong>Extortion by Deserters</strong> village battle in Mount &amp; Blade II: Bannerlord co-op.
</p>

<p align="center">
  <a href="https://github.com/Enes-Balaban17/BannerlordTogetherCrashGuard/releases/latest">Download the latest release</a>
  · <a href="WORKSHOP.md">Steam Workshop publishing</a>
  · <a href="INVESTIGATION.md">Technical notes</a>
</p>

## What it fixes

- **Empty village battle.** The quest raid's campaign state stays available while Bannerlord spawns the player and both armies.
- **Crash when leaving the battlefield.** The raid stays protected through agent cleanup. If a troop's campaign entry is missing at cleanup, the patch uses the mission character to preserve a living hero's health and lets the game finish closing the mission.
- **Deployment fallback.** On a Bannerlord Together gameplay host, the patch catches nonfatal managed exceptions from deployment setup and completion. If the battle opens with missing troops or a missing player, it records a `BROKEN_START_…` entry. This fallback can leave the battle unplayable.

## Compatibility

| Component | Target |
| --- | --- |
| Mount & Blade II: Bannerlord | **v1.4.8.119303** on Windows x64 |
| Bannerlord Together | **v0.5.0.1**, Steam Workshop build |
| Harmony | **v2.4.2.248** |

The original spawn fix was confirmed in a host session. The exit fix and renamed release passed the Windows .NET Framework patch-binding tests. A replay of the affected quest exit and a two-player regression test remain open. This patch targets the quest lifecycle and the two deployment methods.

## Install

1. Extract the release ZIP into the Bannerlord installation. The launcher manifest should be at `Modules\BannerlordTogetherCrashGuard\SubModule.xml`.
2. In **Singleplayer → Mods**, enable **Bannerlord Together Crash Guard** below **BannerlordTogether**. Keep **BLT Deployment Crash Guard** disabled.
3. If upgrading from **BLT Village Battle Fix**, remove the old copy from `Modules` and enable the new entry. Because the module ID changed in v0.2.3, an existing save may show the former module as missing. This mod adds no save data.

## Logs

Open `%LOCALAPPDATA%\BannerlordTogetherCrashGuard\battle.log`.

| Log entry | Meaning |
| --- | --- |
| `Loaded v0.2.3` | Harmony patches loaded |
| `Inactive: Unsupported … SHA-256` | Installed binary differs from the supported build |
| `[DEPLOY-GUARD] BROKEN_START_…` | Deployment exception was caught and spawn checks found a problem |
| `[QUEST-RAID-EXIT] completed` | Mission cleanup returned. Check the quest result on the campaign map. |

When reporting an issue, include the game and mod versions and the relevant section of the log. Remove personal details before posting logs publicly.

## Build from source

PowerShell with an x64 .NET SDK and the target game and mods installed.

```powershell
.\build.ps1 -GameDir "D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord" -SmokeTest
```

Use `-HarmonyBin` and `-TogetherBin` if the dependency DLLs are outside the expected Steam Workshop layout. The build creates a release ZIP in `dist`. The smoke test checks Harmony bindings and the deployment diagnostic classifications without starting the game.
