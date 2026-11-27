# CombatAI

Overhauling Bannerlord's low-level infantry AI: aggressive instead of passive.
Vanilla melee AI spends most of its time blocking and circling — this mod gives
infantry real attack initiative (tempo and turn-taking so agents attack in
rhythm, riposte after blocks, press advantages) *and* a defensive brain that
scales with the situation:

- **1v1** — directional block/parry reads, dodges, and per-attack-type "fall
  for" chances so held attacks and feints genuinely bait defenders instead of
  everything being blocked.
- **2v1** — a defender layer: the outnumbered agent targets only the enemies
  attacking it, keeps them in view with a free camera, defends first, while
  coordinated attacker positioning keeps it from being surrounded.
- **Group fights** — fluid, coordinated target selection (swaps, cover
  handoffs, an attackers-per-target cap, "enemy on me" priority) layered on
  native targeting and movement, plus sudden quick-switch attacks that are
  harder to defend than a swing from a known target.

.NET Framework 4.7.2 · Harmony 2 · MCM v5 · builds against the installed game's
TaleWorlds.* assemblies; output is a module DLL.

## How it works

- `CombatAiMissionBehavior` attaches a `CombatAiOverrideComponent` to every agent
  in supported modes (duel, spar, group fight, custom battle).
- The component intercepts native AI input callbacks (`OnAIInputSet`) and drives
  per-frame combat flags (attack / defend / move bits) from `ComputeDesiredFlag`.
- A duel FSM (Approach -> Windup -> Release -> Recover) picks attack kinds
  (quick, normal, held, feint) and decides when to swing, wait or reposition.
- A defense layer reads enemy swing directions/stages and issues mirrored
  blocks, parries and dodges, with per-opponent modeling.
- Group modes layer target selection, a defender FSM for 2v1s, coordinated
  target swaps and rigged movement on top of native locomotion.

## File map

**Core FSM & combat brain**
- `CombatAiOverrideComponent.cs` — per-agent override: input interception, duel
  FSM, attack/release control, dodge execution, movement writes. The heart.
- `CombatAiOverrideComponent.Brain.cs` — initiative/tempo model, block-roll
  outcomes, held-vs-normal read, post-block follow-ups.
- `PlayerCombatModel.cs` — per-opponent modeling (habit reads; used for both
  player-facing and AI-vs-AI fights).
- `EngagementMap.cs`, `GroupEngagement.cs` — who-is-fighting-whom bookkeeping.
- `CombatAiTune.cs`, `CombatAiSettings.cs` — all tunables; aggressive group
  profile apply/restore; MCM settings.

**Dodges & spatial movement**
- `CombatAiOverrideComponent.AngleDodge.cs` — directional dodges
  (side/back/angle) and kick dodges.
- `AgentClearance.cs`, `ClearanceMath.cs`, `TacticalMovement.cs` — spatial ring
  model: nearby bodies, walls, blocked directions, learned avoidance.

**Group fight**
- `CombatAiOverrideComponent.GroupFight.cs` — movement orchestration:
  native-locomotion passthrough, intent translation while steering facing,
  rigged 2v1 (defender + both attackers), closing assist.
- `CombatAiOverrideComponent.GroupTarget.cs` — target scoring on top of
  native's pick, defender layer (2+ attackers), quick-switch surprise strikes,
  coordinated swaps, cover handoffs.
- `GroupTargeting.cs`, `GroupLineup.cs`, `GroupFormationAdvance.cs`,
  `CombatAiOverrideComponent.GroupLine.cs` — lineup and advance phases of
  staged group fights.

**Arena & spar**
- `ArenaDuoCoordinator.cs` — 2v1 drill partner coordination.
- `CombatAiOverrideComponent.SparRig.cs`, `SparRigPlanner.cs` — scripted spar
  duel (attacker slots around a defender).
- `ArenaCavalryPatch.cs`, `CombatAiOverrideComponent.Cav.cs` — mounted duels.
- `ArenaOverhaulBridge.cs`, `ArenaTeamPracticePatch.cs`, `ArenaShieldPatch.cs`,
  `CombatAiArenaBehavior.cs` — arena integration.

**Native hooks & misc**
- `NativeSteerOverride.cs`, `NativeTargetGuard.cs`,
  `CombatAiOverrideComponent.CbTarget.cs` — custom-battle native target
  replica and a target-release guard.
- `CombatAiConsoleCommands.cs` — `combatai.*` console commands (mode toggles,
  group size, per-feature switches).
- `MasterStrikesBridge.cs`, `MasterStrikesCompat.cs`,
  `MasterStrikesHarmonyPatches.cs` — optional MasterStrikes integration.
- `CombatAiLog.cs`, `CombatAiMemoryBehavior.cs`, `FightScript.cs`,
  `CombatAiSubModule.cs`.

## Systems worth mining

- **Free camera / free attack** — the engine target lock stays on the current
  enemy while the agent's body facing is steered independently: side swings
  land without looking at the target, and a defender keeps multiple attackers
  in view at once. Native movement intent is captured in world space and
  re-expressed in the steered body frame so locomotion stays correct while
  facing elsewhere. Free-target/free-face modes drive locomotion and defense
  with no target lock at all. (`GroupTarget.cs` look computation, the
  translator in `GroupFight.cs`, facing writers in the main component.)
- **Dodge logic** — `AngleDodge` plus the dodge blocks in the main component:
  staged side/back steps triggered on the enemy's windup edge, mirrored to the
  swing direction, kick dodges for kicks.
- **Attack FSM** — Approach think-delay -> Windup with live side-checks ->
  feint redirect -> Release; tempo/quiet-time initiative so agents attack in
  rhythm instead of at random.
- **Defense reads** — ready-stage timing distinguishes held vs normal attacks;
  "fall for" mechanics let defenders misread held attacks sometimes.
- **Group layering** — native movement preserved through a locomotion window;
  layered systems activate only for their role (defender under 2+ attackers,
  quick-switch excursions, rigged 2v1).
- **Coordinated targeting** — swap pairs, cover handoffs, 2-attacker cap,
  "enemy targeting me" priority.

## Notes

- References resolve from the game install via the `BannerlordDir` property.
- Ships inside a host Bannerlord module; `SubModule.xml` and module metadata
  are not included here.
- Shared for design discussion.

## Credits

- **shortjake** — for the SJ debug build of `TaleWorlds.Native.dll` (named
  symbols + assert strings) that made the native-layer research possible, and
  for the NativeHook module whose hook recipe our managed native hooks
  (`NativeTargetGuard`, `NativeSteerOverride`) are based on.
- **EasyHook** — the `LocalHook` infrastructure used by the native hooks.
- **Harmony**, **MCM** — patching and settings infrastructure.
