# Living Systems

A server-side mod for Empyrion: Galactic Survival (developed on Reforged Eden 2): an offline simulation of traders,
pirates and wrecks that turns into real ships near players, with hailing, trading, escorts, rumours and LCD pages.
Still under active development; this repository is updated now and then. MIT licensed (see LICENSE); unofficial and
not affiliated with Eleon Game Studios (see NOTICE). Written with AI assistance (Anthropic's Claude), directed, tested
and reviewed by the author.

## What it uses

Only the two official modding APIs, server-side (dedicated server + playfield servers). No Harmony or other patching,
no IL injection, no native code (no DllImport), no reflection on game objects, no client component, no game files
shipped, EAC untouched. References: ModApi.dll, Mif.dll, UnityEngine.CoreModule.dll (vector maths) — all from the
installed game, not included.

| Feature | Official calls |
|---|---|
| Ship flight (autopilot) | `IEntity.Move(Vector3)`, `MoveForward`, `MoveStop`, `IEntity.Rotation` / `Forward` / `Position`, driven from `IModApi.Application.Update` |
| Spawning / removing ships, wrecks | legacy `Request_Entity_Spawn` (normal prefabs by name), `Request_Entity_Destroy`, `Request_NewEntityId` |
| Stations / galaxy | legacy `Request_GlobalStructure_List`; the save's own Sectors / playfield template YAML, read-only as text |
| Dialogs (hail: talk / trade / jobs) | v2 `IApplication.ShowDialogBox` |
| Trade window | legacy `Request_Player_ItemExchange`, `Request_Player_Info`, `Request_Player_AddCredits` |
| Chat | v2 `IApplication.SendChatMessage` |
| LCD pages, switches | v2 structure devices (LCD text), control-panel signals |
| Wreck contents | v2 `IContainer` content, `IStructure` / `IBlock` |
| Dedi ↔ playfield messages | v2 `IModApi.Network` |
| Game events (who shot whom) | v2 `GameEvent` (parsing the events' own text) + `IStructure.BlockCount` |

The loader (`src/Loader`) hot-reloads the mod's own logic assemblies from its own folder (`Assembly.Load` of our own
dll bytes) so changes don't need a server restart.

## How the flying works

There is no "fly to X" in the API; the mod has its own closed-loop controller (`src/Logic/FlightDriver.cs`). Per ship,
every few frames: pick a target point from the ship's order (berth, warp point, follow, orbit, flee, patrol); compute a
wanted velocity with a braking curve (v = √(2·a·d), capped at cruise speed); turn the nose toward it with a capped turn
rate (`IEntity.Rotation`); convert the needed acceleration into ship-local thrust and send it (`IEntity.Move`). Velocity
is measured from position changes, and each hull's real thrust per axis is learned from how it actually accelerates
(the API exposes neither). Turrets are the game's own AI.

## Layout

- `src/Loader` — the entry the game loads (forwards events to the logic, hot reload).
- `src/Logic` — Dedi and playfield-server code (spawning, flight, live ships, dialogs, trade, LCDs).
- `src/Core` — pure C#, no game types: the offline simulation, flight maths, rules.
- `tests/Core.Tests` — xUnit tests of Core.

## Build

Requires Docker. `EMPYRION_REFS` must point to the game's `…_Data/Managed` folder (for the reference dlls).

    EMPYRION_REFS="/path/to/EmpyrionDedicated_Data/Managed" ./build.sh

Output: `out/Mods/LivingSystems/` (loader dll, `logic/` dlls, mod info yaml) — copy to the dedicated server's
`Content/Mods/`.
