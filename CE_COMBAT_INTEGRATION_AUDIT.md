# CE Combat Integration — second-pass audit

Date: 2026-10-08

This audit compares the RimClone combat integration against Combat Extended Continued's documented combat mechanics and the CE source fragments kept in `clean/Reference/CE`.

Upstream references:
- https://github.com/CombatExtended-Continued/CombatExtended
- https://github.com/CombatExtended-Continued/CombatExtended/blob/Development/README.md
- https://github.com/CombatExtended-Continued/CombatExtended/blob/Development/Source/CombatExtended/ArmorUtilityCE.cs

## Result

The first integration did not overwrite the existing RimClone ballistic/terrain/hitbox foundation. It reused:
- `ProjectileBallistics`
- `ProjectileSystem`
- `UnitSpatialGrid`
- `UnitHitSystem`
- `UnitHealthStore`

The second pass corrected several places where an approximation had been presented as if it were a full CE mechanic.

## Mechanics audit

| CE mechanic | RimClone status | Notes |
| --- | --- | --- |
| Ballistic projectile flight | PRESENT | Existing RimClone system already had speed, mass, diameter, drag, gravity and continuous tracing. |
| Projectile height / 3D trajectory | PRESENT | Existing projectile Z and terrain interval tracing retained. |
| Projectile vs body hitboxes | PRESENT | Existing ellipsoid hit volumes retained; crouching now changes effective hitbox height. |
| Armor penetration / deflection | IMPROVED | Reworked toward CE's deflection + partial penetration model. Separate sharp/blunt penetration is now stored. |
| Armor damage | IMPROVED | Hard/soft armor behavior is represented; armor durability still uses RimClone's own storage. |
| Deflected sharp -> blunt impact | PRESENT | Implemented as a portable approximation of CE's conversion. |
| Partial penetration blunt transfer | PRESENT | Implemented as a portable approximation. |
| Fire modes | PARTIAL | Single/Burst/Auto are present. Current UI/control switching is not yet implemented. |
| Aim modes | PARTIAL | Aimed/Snapshot/Suppress are represented through spread behavior, but CE's full warmup/aim-state model is not yet ported. |
| Target body selection | PARTIAL | Torso/Head/Leg target modes now affect aim point; automatic body-part weighting is not yet CE-level. |
| Recoil | PARTIAL | Recoil is stored and increases spread; full CE recoil pattern / animation model is not ported. |
| Sway / sight efficiency / skill handling | MISSING | Needs a dedicated shooter-state accuracy model. |
| Crouching in combat | PRESENT | Added to posture and used by hitbox / vision height; movement/animation behavior remains simple. |
| Suppression near-miss | PARTIAL | Near misses now add suppression. CE's richer danger/airborne factors and tactical helper behaviors are not all ported. |
| Suppression -> reduced accuracy | PRESENT | Suppression modifies spread. |
| Suppression -> seek cover | PRESENT | Strong suppression causes AI cover behavior. |
| Suppression -> panic / unresponsive state | PARTIAL | State exists, but full shell-shock/mental-state behavior is not ported. |
| CE cover scoring | PARTIAL | RimClone uses its existing radial cover search; CE's detailed fill/path/danger/shield scoring is not ported. |
| Smoke as counter to suppression | MISSING | No smoke tactical action yet. |
| Fire-mode targetting mode UI | MISSING | No player gizmo equivalent yet. |
| Ammo requirement | PRESENT | Existing weapon magazine/reserve system retained. |
| Ammo sets / interchangeable ammo | MISSING | Current weapon fires `DefaultAmmunition` only. |
| Opportunistic / partial reload | MISSING | Latest CE has opportunistic reload thresholds; not yet ported. |
| Ammo-specific modifiers | PARTIAL | Projectile now carries penetration, drag, suppression and bleed metadata; broader CE modifier surface is still missing. |
| Pellets / shotguns | MISSING | `pelletCount` not yet simulated. |
| Secondary damage | MISSING | CE supports chained secondary damage entries. |
| Explosions / fragments | MISSING | No CE-style fragmentation/explosion kernel yet. |
| Rockets / fuel acceleration / special trajectories | MISSING | No reusable equivalent yet. |
| Shields / projectile interception | MISSING | No shield or interceptor combat subsystem yet. |
| Melee crit | MISSING | Reference exists in `clean`, but RimClone melee does not call a CE-style resolver yet. |
| Melee parry | MISSING | Not integrated. |
| Riposte | MISSING | Not integrated. |
| Dodge | MISSING | Not integrated. |
| Melee crit effects | MISSING | Blunt stun / sharp AP / animal knockdown are not integrated. |
| Detailed organ bleeding | IMPROVED | Internal organs now bleed more strongly and keep bleeding after destruction. |
| Stabilization / medical treatment | MISSING | No medicine-based suppression of bleeding yet. |
| Body-part coverage / organ hit weighting | PARTIAL | Body parts and organs exist; CE-level torso/vital weighting is not yet present. |
| Natural armor / body-part armor density | PARTIAL | Existing body penetration losses remain, but CE's natural armor density model is not fully ported. |

## Important finding about the first integration

The previous `DamageSystem` used RimClone's percentage-like `DamageAbsorption` model. That model was replaced as the primary ballistic resolution path with `ArmorResolver`.

This was intentional, but `DamageAbsorption` should now be treated as legacy/compatibility data rather than as the core physical armor rule.

## What should be ported next

### Tier 1 — core gunplay
1. Full accuracy model: sway, recoil, movement, aim time and shooter skill.
2. Proper aim-mode state machine.
3. Ammo sets and ammo switching.
4. Pellets / multi-projectile shots.
5. Secondary damage / explosions / fragments.

### Tier 2 — combat depth
1. Shield/interceptor layer.
2. Full cover rating and suppression tactics.
3. CE melee: dodge -> parry -> riposte -> crit.
4. Better anatomical hit weighting and organ consequences.
5. Reload thresholds and tactical reload behavior.

### Tier 3 — special combat
1. Rocket acceleration and special trajectory workers.
2. Airburst / fuze behavior.
3. CIWS / projectile interception.
4. Vehicle / building armor penetration.
5. Area effects and persistent combat hazards.

## Design rule for RimClone

Do not copy the CE object model.

Keep the RimClone data-oriented boundary:

`Weapon/Ammo data -> ProjectileStore -> ProjectileSystem -> Hit -> Armor -> Damage -> Health -> AI`

Game-specific world queries should supply terrain, cover, target geometry and faction rules. The combat kernel should not know about RimWorld's `Pawn`, `Thing`, `Map`, `Verb`, `Job` or `Def`.
