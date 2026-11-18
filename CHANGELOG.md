# Changelog

## v0.3.2 — reduce recurring work and correct income calculation

- Removed periodic all-NPC appearance scans and complete crafted-item scans. Editor saves and crafted-item registration publish changes; join/Sync still sends full state.
- Batched full NPC snapshots across frames and bounded compatibility receive work per frame. Replaced extra periodic full kingdom refreshes with war/peace-triggered or explicit refreshes.
- Cached gross-income previews between hourly refreshes instead of recalculating all clan income every publish.
- Moved sharing into the actual native income contribution before expenses. The previous postfix queried income again after wallet withdrawals, so its second result could differ from the amount collected.
- Clients use the host income snapshot without withdrawing the same asset wallets independently. Exactly one native daily gold-transfer call is routed to the local human recipient in shared-clan client mode.
- Split common garrison funding debits between participating humans. Personal party expenses and other local costs remain; shared income does not guarantee a positive daily balance.
- Added signed integer allocation with conserved remainders, daily income/net-transfer logs and rate-limited slow-publish diagnostics.

Validation: clean compilation, core smoke checks, 48 actual optional bindings, post-withdrawal income and no-double-share regressions, unchanged unrelated-clan accounting, signed amount conservation for 2–4 players, and exactly one native daily transfer rewrite. Live frame-time measurements and daily income replay are still pending. The user's reported 3–4-second stalls have not been reproduced locally; this build removes recurring work identified in the current code without claiming all stalls are resolved.

## v0.3.1 — settings deltas and missing faction geography

- Included a self-contained Windows setup executable with automatic download-zone removal for its installed DLL/manifest and backup/rollback of an existing installation. No manual PowerShell unblocking is required.

- Added per-connection state caches. Unchanged settings are no longer transmitted every two seconds; host edits send changed values only. A failed ordered send does not advance the cache.
- Added full compatibility refresh after native BT save-resync and isolated-peer preparation. New connections receive complete state. Client handshakes stop after the host state arrives.
- Kept a local periodic comparison for mods without a settings-change event. Moving patrols and changing world state still update independently of settings.
- Added a co-op guard for missing settlement/faction geography in the native geographical valuation multiplier. Unavailable Diplomacy fief buyers sort last and are rejected before a barter can transfer a fief. Client corruption simulation follows host authority.
- Added regression checks for unchanged-state silence, exact changed-value dispatch, failed-send retry, cache reset, missing geography and rejected invalid buyers. Optional bindings now cover 44 actual game/mod methods.

### Submitted crash evidence

The supplied `newlogs/battle.log` records v0.2.4, not v0.3.0. `report 1.txt` captures a 17 November daily-tick crash in native `GeographicalAdvantageForFaction`, called by Diplomacy's influence-corruption fief valuation. The supplied report does not identify the exact null runtime object; the guard addresses the missing-geography condition in that path. `report 2.txt` captures a 16 November Xorberax shoulder-camera null mission crash with no Crash Guard in its active-mod list; that transition already has a camera guard in the current build.

The RGL files also contain banner XML schema errors and repeated captivity-end menus. These do not establish a distinct village battle crash stack. This release does not claim all village crashes are resolved. The managed tests reproduce and guard the missing-geography boundary without launching Bannerlord; full campaign and village replay remain pending.

## v0.3.0 — co-op compatibility development build

Added a bounded, versioned compatibility channel over Bannerlord Together's existing reliable transport. The host distributes runtime settings and state to authenticated gameplay peers; missing optional mods are skipped. Local settings are restored when leaving the session.

Added adapters for empty-account Xorberax interest, BetterTime multipliers, Campaign Tweaks limits, Improved Garrisons settings/patrol projections, native Diplomacy relation reconciliation, Modular Smithing settings/definitions, Character Reload appearance updates, player culture profiles, per-slot starting loadouts, and host dismemberment visuals. Together Lords ignores this extension's packets.

Extended village deployment/teardown tracking and prevented campaign casualty simulation during pending linked village missions. Added guarded Diplomacy messenger conversation entry, equal gross shared-clan income, native battle reward redistribution, single-owner existing lord custody and dead projection encyclopedia hiding.

This build introduces campaign save bookkeeping. Back up the campaign and install the same build on every participant before testing. See README for feature boundaries; this is not a claim of complete compatibility with every feature of the listed mods.

### Validation performed

- Release build: zero warnings and errors.
- Core Harmony installation/unload, captivity IL rewrite, camera fallback and deployment diagnostics passed on Windows .NET Framework.
- Optional bindings installed on 38 actual methods from the installed game and mod binaries.
- Compatibility packet UTF-8 round trip and malformed/foreign/trailing-data rejection passed.
- Empty-account interest regression passed; funded balances and loan data were preserved.
- Integer, Boolean, float and enum host settings transfer/disconnect restoration passed.
- Reward allocation conserved quantities and selected one owner for a unique unit for 2–4 players, all starting remainders and totals 0–1024.
- Both native merged-battle lord cleanup release paths were rewritten on the installed Bannerlord Together binary.

### Live verification still required

No game was launched for these checks. Use a copied campaign for the following scenarios:

1. Host and client use different local BetterTime/settings values; change the host value, verify movement/limits, then disconnect and verify local settings restoration.
2. Empty a bank, advance time, and check the actual balance and interest display on each peer.
3. Create/remove a patrol and edit its settlement training template on the host; verify remote position/roster/settings without rejoining.
4. Declare war and make peace; verify relations converge on both peers. Send a messenger to a field hero and a settlement hero, finish each dialogue, and confirm campaign control returns.
5. Edit player and NPC appearance; craft/equip a Bahamut-based item and verify it on a joining peer with matching assets.
6. Replay raid, coercion and village-defense mission entry/exit. Verify no pre-battle troop casualties and retain logs for any crash.
7. Run 2-, 3- and 4-player battles with multiple enemy lords. Record total loot/prisoners before/after, assign every lord once, then test release/ransom, escape, disconnect and reconnect.
8. Compare shared-clan gross income and each player's expenses over a daily tick; disconnect the final client and verify the host no longer divides income.
9. Reconnect repeatedly and check the encyclopedia; use explicit IDs for older untracked dead projections.

Logs: `%LOCALAPPDATA%\BannerlordTogetherCrashGuard\battle.log`.
