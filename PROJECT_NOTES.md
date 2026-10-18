# Wildwood: project notes

Read this first in any new chat. It records what the game is, the decisions already made, and how the code is organized.

## The game
- **Pitch:** Astroneer-style exploring, digging and base building in a Valheim-style stylized world. Co-op multiplayer.
- **Core idea:** almost everything is breakable, Minecraft-style, but with Teardown-scale 10 cm blocks. Building, mining, chopping trees and destruction all run on one block system.
- **Building:** place by click, drag a line, drag a rectangle, plus smart tools (plane lock, brush sizes, box, wall, mirror, blueprints, undo).
- **Physics:** every material has weight and strength. Unsupported sections break off and fall, shatter into "Lego explosion" fragments, and leave pickups. A vacuum tool collects debris.
- **Shapes:** blocks aren't just cubes. Each block stores which of its 8 corners exist (deleting a corner gives a triangular cut, deleting an edge gives a slope).
- **Look:** painterly golden-hour world (soft fog, light shafts, dense grass, blobby tree canopies). The block builds read as little toy bricks with soft edges.

## Settled decisions
- **Engine:** Unity 6.3 LTS (6000.3.x), URP with Forward+.
- **World split:** smooth diggable terrain (Astroneer style) underneath, 10 cm block objects on top (builds, trees, boulders, ore, ruins). The whole world is NOT 10 cm blocks; that would be tens of billions of blocks.
- **World size:** keep the playable area within a few km of the origin, so floating-point precision never becomes a problem and we never need a "floating origin".
- **Blocks:** `Block` struct is 4 bytes: `ushort Material` (0 = air), `byte Corners` (8-bit corner mask, 0xFF = full cube), `byte Reserved` (future: half-step corners or damage). This is also the save and network format.
- **Chunks:** 32 x 32 x 32 blocks (3.2 m). One mesh + one collider per chunk. Edits rebuild only the touched chunks, with Burst jobs.
- **Corner shapes:** shape = connect the remaining corners. All 256 masks get their mesh, collision, volume and face coverage precomputed once in a lookup table. Only 45-degree slopes for now; half-step corners may come later via `Reserved`.
- **Structure (weight and strength):** two layers. (1) Connectivity: pieces no longer touching the ground break off. (2) Load: weight flows down through ~40 cm cells; overloaded connections snap. Connection strength = material strength x touching face area. Creaks, dust and cracks warn before a collapse. Blue-to-red stress view in build mode.
- **Debris:** big falling pieces are simulated by the host and synced. Small fragments are visual-only on each machine. The host creates pickups. Dynamic pieces use box colliders (Unity can't move non-convex mesh colliders).

## Multiplayer rules (follow these for every feature)
1. **The host owns the world.** Clients ask; the host checks, applies and broadcasts. Only the host runs the structure check.
2. **Send commands, not blocks.** "Fill box A..B with stone" is one small message, never thousands of block updates. Block positions are integer grid coordinates.
3. **World = seed + edits.** Joining players generate untouched terrain from the seed and download only changed chunks. Saves use the same data.
4. **Debris tiers:** big pieces synced, fragments local-only, pickups host-owned.
5. **Players own their own movement** (NetworkTransform in Owner mode). Fine for friends-only co-op.
6. **Keep world logic out of MonoBehaviours** (in `Wildwood.Core`) so a dedicated server stays possible.
7. **Test every feature with two players** (Multiplayer Play Mode) before calling it done.

Networking: Netcode for GameObjects 2.x in normal host mode (not Distributed Authority). Unity Transport. Lobbies and relay (Multiplayer Services) later; Steam invites once there's a Steam page.

## Code layout
Everything we make lives in `Assets/_Wildwood/`, separate from Asset Store imports.

| Folder | Assembly | What goes there |
|---|---|---|
| `Scripts/Core` | `Wildwood.Core` | Block data, chunks, corner shapes, meshing, structure math. No MonoBehaviours, no networking. Burst/Collections/Mathematics allowed. |
| `Scripts/Net` | `Wildwood.Net` | Sessions, network messages, world sync. References Core + Netcode. |
| `Scripts/Game` | `Wildwood.Game` | Players, tools, rendering, UI. References Core + Net + Netcode + Input System + Cinemachine. |
| `Scripts/Editor` | `Wildwood.Editor` | Editor-only tools (scene builder, debug windows). |
| `Tests/EditMode` | `Wildwood.Core.Tests` | Automated tests for Core (Window > General > Test Runner). |

Namespaces match: `Wildwood.Core.Blocks`, `Wildwood.Core.Meshing`, `Wildwood.Net`, `Wildwood.Game.Blocks`, `Wildwood.Game.Player`, `Wildwood.Game.UI`, `Wildwood.EditorTools`.

### Key classes
- `Core/Blocks/Block.cs`: the 4-byte block, `CornerBits`, `BlockConstants`.
- `Core/Blocks/CornerShapes.cs`: the 256-entry shape table (convex hull of the remaining corners), face coverage for culling, `BlockFaces`.
- `Core/Blocks/BlockWorld.cs` + `ChunkData.cs`: chunked storage, dirty-chunk tracking, sanitizing (unknown materials and flat shapes become air).
- `Core/Blocks/BlockEdit.cs`: edit commands (`Fill` box, `Chisel` corners) and `BlockEditor` (validate + deterministic apply).
- `Core/Blocks/ChunkSerializer.cs`: run-length encoding for snapshots (and later saves).
- `Core/Blocks/ShapeStamp.cs`: build shaped blocks from an "is this point inside?" rule (slopes, pyramids, rocks). `DemoStructures.cs` uses it.
- `Core/Meshing/ChunkMesher.cs`: chunk to triangles, flat-shaded, palette UVs. Plain C# for now; Burst + greedy merging later.
- `Net/BlockWorldSync.cs`: host-authoritative edits and join snapshots over Netcode named messages.
- `Game/Blocks/BlockWorldView.cs`: owns the world on this machine, rebuilds chunk meshes/colliders. `BlockPalette.cs` makes the color texture.
- `Game/Blocks/BlockInteractor.cs`: aiming, place/remove/chisel, materials, brush sizes, HUD (on the player prefab).

## How to test
1. **Build the scene:** menu **Wildwood > Build Sandbox Scene** (rebuilds `Scenes/Sandbox.unity` and `Prefabs/Player.prefab` from code).
2. **Second player:** Window > Multiplayer > Multiplayer Play Mode, tick Player 2.
3. **Play:** press Play, click **Host** in one window and **Join** in the other.
4. **Controls:** WASD move, mouse look, Space jump, Shift sprint. Esc frees the mouse; click to capture it again.
   Building (temporary until step 4): left click remove, right click place, 1-8 material, scroll brush size (1/2/4/8 blocks), C toggles chisel (left click deletes the nearest corner).
5. **Tests:** Window > General > Test Runner > EditMode > Run All.

## Build order
1. ~~Setup pass~~ (done)
2. ~~Two players walking around a shared test scene~~ (done)
3. **Blocks:** chunk data, corner-shape table, meshing, place/remove/chisel synced between players, join snapshots (built, needs testing)
4. **Building tools:** line, rectangle, box, plane lock, brush sizes, preview, undo, chisel, auto-slope
5. **Breaking off:** disconnected pieces fall with physics
6. **Shatter, pickups, vacuum**
7. **Weight and strength,** stress view, collapse warnings

After that: terrain, world generation, art pass, survival loop (crafting, inventory, progression).

## Project settings worth knowing
- Run In Background is on (needed for two windows), and the game starts windowed at 1280x720 for testing.
- Input uses the project-wide actions in `Assets/InputSystem_Actions.inputactions`.
- Removed packages: Visual Scripting, Unity Version Control (we use git + GitHub, with Git LFS for art and audio).
