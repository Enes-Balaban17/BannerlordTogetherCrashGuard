<p align="center">
  <img src="docs/cover.jpg" alt="Bannerlord Together Crash Guard" width="900">
</p>

<h1 align="center">Bannerlord Together Crash Guard</h1>

<p align="center">
  Crash protection and host-controlled compatibility adapters for <strong>Bannerlord Together</strong> co-op.
</p>

<p align="center">
  <a href="https://github.com/Enes-Balaban17/BannerlordTogetherCrashGuard/releases/latest">Download the latest release</a>
  · <a href="WORKSHOP.md">Steam Workshop publishing</a>
  · <a href="INVESTIGATION.md">Technical notes</a>
</p>

## What it fixes

**v0.3.1 is a development build for coordinated co-op testing.** Compilation, patch bindings against the installed game/mod binaries and managed regression checks pass. The new adapters have not yet been verified in a live multiplayer session. Keep a separate backup of your campaign before testing.

Settings are sent when a player joins, after the native BT save-resync flow, or when the host changes a supported value. A local comparison still runs periodically to detect edits made by mods without change events; unchanged values produce no compatibility state packets. Patrol positions, income and other changing world state remain eligible for updates.

| New adapter | Implemented behavior |
| --- | --- |
| Xorberax's Legacy | Clears stale interest when a bank account is emptied. Existing camera protection remains. This does not merge every player's banking ledger. |
| BetterTime | Shares the host's fast-forward multiplier; client multiplier requests follow Bannerlord Together's client time-control permission. |
| Campaign Tweaks | Shares runtime settings and host companion/workshop limits in native shared-clan mode. |
| Improved Garrisons | Shares global and settlement settings, training templates, and host patrol/recruiter positions and nonhero rosters. Host runs the simulation; remote map parties are projections. Full remote management commands are not implemented. |
| Diplomacy | Periodically reconciles native kingdom relations and suppresses duplicate client-side daily diplomacy simulation. Guards settlement valuation when faction geography is missing and excludes unavailable buyers from corruption-related fief transfers. Remote messenger conversations use a guarded field conversation instead of settlement location lookup. This does not synchronize every Diplomacy feature. |
| Modular Smithing 2 | Shares host crafting settings and crafted item definitions, including definitions using installed Bahamut donor items. All players still need the same item assets. |
| Character Reload | Relays body, name, culture, race, level, skills and attributes for existing NPC heroes; refreshes native player appearance broadcasts. Perks, traits, focus and equipment editing are not fully covered by this adapter. |
| Custom Cultures / Custom Starting Loadout | Stores culture profiles by authenticated player identity and applies culture feat overrides to remote player projections. Tracks initial loadout per character slot. |
| DismembermentPlus | Relays host dismemberment visuals for synchronized battles, avoids independent client rolls and co-op slow motion, and logs failures instead of opening a blocking error box. |
| Together Lords | Keeps this mod's extension packets out of Together Lords' packet dispatcher. This is transport coexistence, not a replacement for its synchronization. |

Additional development features:

- **Village battle transitions:** extends deployment/teardown tracking to village encounters and holds campaign simulation casualties while a linked player battle is pending. Intentional autoresolve remains available.
- **Battle rewards:** divides the host's newly earned items, gold and ordinary prisoners among compatible participating players through Bannerlord Together's native reward flow. A captured lord uses the existing hero object and one assigned recipient; no hero copies are created. Disconnection, ransom, escape and multi-lord battles still need live testing.
- **Shared clan income:** replaces the native fixed two-player split with equal gross clan income shares for connected compatible players in shared-clan mode. Individual expenses remain local, so net gold can differ. This assumes all participants share the host clan.
- **Encyclopedia:** hides dead co-op projection heroes identified during despawn or by exact native log evidence. Real heroes are retained, not deleted. Old copies without log evidence require explicit identification.

The messenger adapter addresses unsafe conversation entry conditions; it is not proof that every reported native freeze or crash has been resolved. Include a fresh crash report if it reproduces.

- **Empty village battle.** The quest raid's campaign state stays available while Bannerlord spawns the player and both armies.
- **Crash when leaving the battlefield.** The raid stays protected through agent cleanup. If a troop's campaign entry is missing at cleanup, the patch uses the mission character to preserve a living hero's health and lets the game finish closing the mission.
- **Deployment fallback.** On a Bannerlord Together gameplay host, the patch catches nonfatal managed exceptions from deployment setup and completion. If the battle opens with missing troops or a missing player, it records a `BROKEN_START_…` entry. This fallback can leave the battle unplayable.
- **Xorberax Shoulder Camera compatibility.** During a co-op transition with no current mission, Xorberax uses the normal camera instead of reading the missing mission clock. The hook activates when the compatible Xorberax package is loaded.
- **Leaving captivity in co-op.** The relocation search handles nearby parties without a faction. If relocation still throws a null reference during local player release, native captivity cleanup continues.

## Compatibility

| Component | Target |
| --- | --- |
| Mount & Blade II: Bannerlord | **v1.4.8.119303** on Windows x64 |
| Bannerlord Together | **v0.5.0.1**, Steam Workshop build |
| Harmony | **v2.4.2.248** |

The original spawn fix was confirmed in a host session. The exit fix and renamed release passed the Windows .NET Framework patch-binding tests. A replay of the affected quest exit and a two-player regression test remain open.
The v0.2.4 camera hook was tested for binding with Xorberax’s Legacy **v1.0.8.8**. The two new fixes still need a live co-op replay.

## Install

**Recommended:** download `BannerlordTogetherCrashGuard-v0.3.1-Setup.exe` from [Releases](https://github.com/Enes-Balaban17/BannerlordTogetherCrashGuard/releases/latest), run it, and select the Bannerlord game folder. The installer includes the mod payload, automatically removes the installed module files' Windows download block (`Zone.Identifier`), and keeps a backup of an existing installation. No separate PowerShell or DLL-unblock step is needed. It only unblocks this mod's installed files; it does not change other mods or Windows security settings.

1. Extract the release ZIP into the Bannerlord installation. The launcher manifest should be at `Modules\BannerlordTogetherCrashGuard\SubModule.xml`.
2. In **Singleplayer → Mods**, enable **Bannerlord Together Crash Guard** below **BannerlordTogether**. Keep **BLT Deployment Crash Guard** disabled.
3. Install **the same v0.3.1 build on the host and every client**, with the same optional mod versions and asset packs. Missing optional mods are skipped. Run the setup executable if you prefer installation without manual PowerShell commands.
4. If upgrading from **BLT Village Battle Fix**, remove the old copy from `Modules` and enable the new entry. Because the module ID changed in v0.2.3, an existing save may show the former module as missing. Starting with v0.3.0, compatibility identities and reward/projection bookkeeping are stored as campaign save data.

For older dead encyclopedia duplicates, use the developer console to list exact IDs with `btcg.find_dead_heroes <name>`, then hide only the unwanted IDs with `btcg.hide_dead_heroes <id> [more ids]`. This hides entries and preserves the underlying save objects.

## Logs

Open `%LOCALAPPDATA%\BannerlordTogetherCrashGuard\battle.log`.

| Log entry | Meaning |
| --- | --- |
| `Loaded v0.3.1` | Harmony patches loaded |
| `Inactive: Unsupported … SHA-256` | Installed binary differs from the supported build |
| `[DEPLOY-GUARD] BROKEN_START_…` | Deployment exception was caught and spawn checks found a problem |
| `[QUEST-RAID-EXIT] completed` | Mission cleanup returned. Check the quest result on the campaign map. |
| `[SHOULDER-CAMERA] Used normal camera` | Xorberax skipped the ranged camera adjustment during a co-op transition |
| `[CAPTIVITY-EXIT]` | Relocation encountered missing faction data or failed before release cleanup completed |
| `[COOP-COMPAT]` / `[HOST-SETTINGS]` | Compatibility handshake, state/settings application, or an optional adapter failure |
| `[MESSENGER]` | Remote conversation opened, postponed, declined or failed |
| `[LOOT-SHARE]` | Reward allocation, unique lord assignment, or a failed transaction |
| `[VILLAGE-SIMULATION]` | Casualty simulation held while a linked village mission is pending |

When reporting an issue, include the game and mod versions and the relevant section of the log. Remove personal details before posting logs publicly.

## Build from source

PowerShell with an x64 .NET SDK and the target game and mods installed.

```powershell
.\build.ps1 -GameDir "D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord" -SmokeTest
```

Use `-HarmonyBin` and `-TogetherBin` if the dependency DLLs are outside the expected Steam Workshop layout. The build creates a release ZIP in `dist`. The smoke test checks Harmony bindings, the captivity IL replacement, and deployment diagnostic classifications without starting the game. Pass `-XorberaxBin` with the folder containing `XorberaxLegacy.dll` to exercise the optional camera hook too.

Add `-CompatibilityTest` to exercise all optional bindings, packet validation, empty-account regression, configuration restoration and reward conservation. This check requires the optional mods in the local Steam/game layout; it does not start Bannerlord.
