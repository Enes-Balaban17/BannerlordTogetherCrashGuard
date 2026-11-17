# Extortion by Deserters deployment investigation

## Evidence

- The archive's 58 file hashes matched its manifest. The screenshots show mod versions, load order, installation errors and settings. The crash reports and RGL logs establish mission launch and failure timing.
- Crash Guard source was cloned and inspected at [c5eb0011b6e02c0b426d65b42031589d9497a07d](https://github.com/goobz22/BLTDeploymentCrashGuard/tree/c5eb0011b6e02c0b426d65b42031589d9497a07d). Both supplied DLLs match the pinned release manifest. At 04:49:35 Crash Guard lifted 24 Together patches in auto mode, then at 04:49:37 it suppressed the exception. The suppressed method returns to `OnMissionTick`, which sets `TeamSetupOver` even though initialization aborted. The later forced co-op setting also failed according to the user. The desktop host log and corresponding RGL run at 05:00 show the quest encounter and a native `Battle land_raid` opening with Together's synchronized battle state off.
- The 05:00 Together log shows an incoming quest deserter raid, the defender-side encounter context, and background campaign ticking during the encounter and mission. Its `playerMapEvent=none` field prints the event's string ID with a fallback. New map events can have a null string ID, so this log field alone cannot establish detachment.
- The repeated crash report points to `SetupTeams` IL_0049. The exact installed assembly has the optional enemy-hide call at IL_0044, the start of player-side setup at IL_0049 and the first unguarded use of `InitialPlayerAgent` at IL_0062. A defender with the ordinary quest setup branches over the enemy-hide call. The reports do not include the live reference or the execution stage, so the exact null in these reproductions remains unconfirmed. A missing `InitialPlayerAgent` after player-side spawning is a candidate supported by the native code and analogous upstream traces.

## Code path and candidate fix

The quest starts a village raid with the player on the defender side and adds the quest defender party to the existing event. The native village opener sets the scene level to `land_raid`. The SandBox battle mission builds troop suppliers from the campaign event and initializes attacker and defender teams through `MissionCombatantsLogic`. Its spawn handler sizes initial phases from both campaign rosters. Deployment enables enemy spawning and then player-side spawning. `DefaultBattleMissionAgentSpawnLogic.CheckDeployment` requires enough supplier reservations and team plans before it spawns agents. `SetupTeams` then takes control of the initial player agent for deployment, and `FinishDeployment` hands that agent back and enables battle AI.

Game code still calls `MapEvent.Update` for a raid involving the main party. That call can run raid progression, autoresolution and roster changes while Bannerlord Together drives campaign time in the background. Together has a pre-mission raid hold conditional on the waiting decision state and a separate mission hold conditional on its synchronized battle state. The reported reproduction used a normal quest encounter and a native mission, leaving a gap between those protections.

The module holds periodic updates for the specific quest raid during the local host's active defender encounter and while its tracked `land_raid` mission is active. It uses campaign event object identity and the two quest attacker IDs from the installed game. The pre-mission hold leaves explicit simulation and resolved encounters alone. The tracked mission hold extends through agent teardown in v0.2.2. Explicit campaign result handling, the native deployment and spawn methods, and Together's transport continue through their original code paths. The client runs the observation hooks only.

The diagnostic baseline records whether a periodic update changes the event, player attachment or healthy roster counts. Other probes record raid finalization callers, the mission opener and role, deployment patch owners, team presence and agent counts, initial spawn quotas, and the player agent after each side's setup. In v0.2.0 the host fallback runs after the observation finalizer. It suppresses a nonfatal managed error from either native deployment method and reports the observed state. Partial deployment steps, roster changes and campaign results belong to the native game and Together.

## Verification

- Compiled x64 `net472` against the local v1.4.8.119303 installation, Together build `035beead876d66fb1e91d7282cd98bc4f624430b` and Harmony v2.4.2.248.
- The host reported the quest fix worked with the separate Crash Guard disabled on v0.1.0.
- Attached and removed all seven patch targets in Windows PowerShell's .NET Framework using the installed binaries. Checked that both crash finalizers were present and exercised the exception policy and deployment outcome classifications.
- Live v0.2.0 testing, including suppressing a deployment exception while hosting with a peer, remains open.

## Uthelaim exit crash (75a44bb8)

On v0.2.1 the patch log records 80 defenders, 28 attackers and an active player agent after side setup. RGL confirms normal entry into `land_raid`. At 06:08:07 the managed stack reaches `PartyGroupAgentOrigin.OnAgentRemoved` through `Agent.OnRemove` and `Mission.EndMissionInternal`. The patch's mission-tick postfix appears in the wrapper stack; its deployment crash guard did not run for this exception.

The native callback evaluates `Troop.IsHero` and writes hero health through a second lookup of `Troop.HeroObject`. Those references are resolved from the campaign supplier's roster and can be unavailable during teardown. The report does not identify which agent or dereference failed. The prior version resumed periodic campaign updates at victory while the mission still had live agents. v0.2.2 retains the hold through teardown. A recovery scoped to the tracked quest mission uses the actual mission character to commit hero health when the campaign lookup is missing. A missing regular troop has no hero health to write. Mission cleanup and explicit campaign finalization run normally.

The v0.2.2 Release build and nine-target Harmony smoke test passed. The Uthelaim quest exit and save/reload remain to be verified in-game.

## Client battle exit and captivity release

During a co-op field battle the client left after victory. Bannerlord Together had already cleared the live battle state when Bannerlord reactivated a mission screen. The managed stack points to `XorberaxLegacy.Patches.ShoulderCameraPatch.ShouldRevertCameraForRangedMode` at the read of `Mission.Current.CurrentTime`. The installed Xorberax v1.0.8.8 implementation uses the global mission for this clock. v0.2.4 asks the optional camera helper to revert to the normal camera for a co-op frame with no current mission.

In the earlier session, the client reached the release menu and its first-chance log recorded a null reference in `MobileParty.TeleportPartyToOutSideOfEncounterRadius`. No fatal stack identifies the exact dereference. The native relocation loop calls `IsAtWarWith` on a nearby party's faction without checking for a faction. The host also logged a leaderless co-op projection nearby in time. v0.2.4 handles missing faction data during the local co-op player's captivity release and leaves the native position search in place. If relocation still throws a null reference, its narrow fallback lets native release processing continue. Captor state and events remain owned by Bannerlord and Bannerlord Together.

The build smoke test confirmed the native IL replacement, its fallback and the optional camera hook against the installed Xorberax v1.0.8.8 assembly. For live validation, first replay a co-op victory and leave the battlefield with Shoulder Camera enabled on the client. Then reproduce release from a mobile captor and confirm that Continue returns control to the client, the hero is free, and the host receives the updated state. Live co-op testing remains open.

## Pinned runtime inputs

The module checks these binary SHA-256 values at startup because its patch targets include private game methods.

| File | SHA-256 |
| --- | --- |
| `bin\Win64_Shipping_Client\TaleWorlds.MountAndBlade.dll` | `19387F31557FF840D14F378F6BBDF1D58FCFF406AB9FF2294DFBB3E49A50B87E` |
| `bin\Win64_Shipping_Client\TaleWorlds.CampaignSystem.dll` | `1F8E33E2ED73E6EC653D7629180AFB70649DDC6E5BD1657A802A264EFDA1C3AE` |
| `Modules\SandBox\bin\Win64_Shipping_Client\SandBox.dll` | `587C6E94C425745C1478F632F3B66226FF8298666CB27E8E6D9A62488DF5956A` |
| Workshop `3761555398\bin\Win64_Shipping_Client\BannerlordTogether.dll` | `BB1D1998CF78D22C6C4CCABE3DD6D9A9E10BC8D3A5FC01B28DCE1C90CE4508DB` |
