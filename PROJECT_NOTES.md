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
- **Undo:** the host records every block each edit changes (`BlockChange`) in a per-player `UndoHistory` (40 edits / 1.5M blocks per player). Undo only restores blocks that still look how that edit left them, so it never erases another player's later work. Clients get the result as fresh copies of the touched chunks.
- **Edit size limits:** fill boxes up to 128 blocks per side and 64^3 blocks total (drag-built walls and floors); chisel regions up to 16.
- **Camera:** one Cinemachine third-person camera; first person is the same camera with its shoulder offset, arm and distance blended to zero (`PlayerCameraRig.SetFirstPerson`). In first person your own renderers switch to shadows-only and the body faces the look direction.
- **Big chisel:** chisel works on brush-sized cube regions. The region counts as one big block: deleting its corner reshapes every small block inside. Shape planes only use normals with -1/0/1 entries, so this is exact with integer math (`CornerShape.Planes`, `ContainsScaled`).
- **Terrain:** a smooth 3D density field (positive = solid) sampled every 0.5 m, 16 m chunks (32 cells), meshed with surface nets (one vertex per surface cell, shared vertices, gradient normals). Generated from the seed on every machine; nothing terrain-related is sent or saved yet. It's volumetric on purpose so digging can be added by editing density.
- **Biomes:** plains (low rolling hills, golden grass) and forest (hillier, dense trees). `TerrainGenerator.Forest()` = 0..1. Spawn has a flat clearing (radius 22 m, height 0) and is guaranteed forest to the east (+X) and plains to the west.
- **Trees:** generated per 4 m cell from the seed. For now they're low-poly scenery (tapered trunk + lumpy sphere canopy or stacked cones) with capsule trunk colliders. Plan: when hit, a tree turns into real blocks so it can be chopped and fall ("lazy voxelization").
- **Grass:** GPU-instanced tufts (`Graphics.RenderMeshInstanced`) within ~45 m of the camera; normals point up for soft lighting. Wind needs a custom shader (art pass).
- **Colors:** everything uses stock URP Lit materials reading small generated palette textures through UVs (`BlockPalette`, `NaturePalettes`). One draw call per chunk.
- **Saving:** only the host saves: seed + all block chunks (`WorldSaveFormat`), to `%USERPROFILE%/AppData/LocalLow/ShingIndustries/Wildwood/Worlds/sandbox.wwworld`. Saves on new world, every 60 s, and when the session ends. Written to a temp file first.
- **Breaking off (built):** after every edit or undo, the host flood-fills from the changed blocks (`StructureCheck`). A group that can't reach the ground breaks off. Ground = the generated terrain height under any of a block's 4 bottom corners, with 6 cm of slack because the visible terrain is a smoothed mesh. Blocks only hold each other up through faces that share area (two slopes meeting along an edge don't count). Groups over 40k blocks or 256 blocks per side are treated as held up for now. A loose group becomes a `BlockPiece` (blocks cut out of the world into a small local box), sent to clients once; the host simulates it as a rigidbody (mass from material density, greedy box colliders, max 96) and streams position/rotation ~20x/s on an unreliable channel; client copies are kinematic and glide to it. Max 60 pieces (oldest removed); left click clears one. Pieces aren't saved yet.
- **Character (built):** `PlayerAvatar` builds an original low-poly Norse-style character from tapered boxes at runtime (no model file), dressed in 32 px point-filtered textures from `PixelTextures` (wool with a woven band, leather, hair strands). Colors per player from their client id. It animates itself from its own movement (walk/run cycle, idle breathing, air pose from a ground probe), so remote players animate without extra network data. Eye/camera height 1.66 m. Replace with a rigged model + Animator in the art pass.
- **Look:** golden-hour sun in front of spawn (backlit), procedural sky, linear warm fog (30-140 m, also hides the terrain load edge), trilight ambient (no bake needed), global volume: neutral tonemapping, bloom, warm color grading, light vignette. SMAA on the camera.

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
| `Scripts/Core` | `Wildwood.Core` | Block data, chunks, corner shapes, meshing, terrain generation/meshing, save format, structure math. No MonoBehaviours, no networking. Burst/Collections/Mathematics allowed. |
| `Scripts/Net` | `Wildwood.Net` | Sessions, network messages, world sync. References Core + Netcode. |
| `Scripts/Game` | `Wildwood.Game` | Players, tools, rendering, UI. References Core + Net + Netcode + Input System + Cinemachine. |
| `Scripts/Editor` | `Wildwood.Editor` | Editor-only tools (scene builder, debug windows). |
| `Tests/EditMode` | `Wildwood.Core.Tests` | Automated tests for Core (Window > General > Test Runner). |

Namespaces match: `Wildwood.Core.Blocks`, `Wildwood.Core.Meshing`, `Wildwood.Core.Terrain`, `Wildwood.Net`, `Wildwood.Game.Blocks`, `Wildwood.Game.Persistence`, `Wildwood.Game.Player`, `Wildwood.Game.Terrain`, `Wildwood.Game.UI`, `Wildwood.EditorTools`. (Careful: `UnityEngine.Terrain` is a Unity class; we never use it.)

### Key classes
- `Core/Blocks/Block.cs`: the 4-byte block, `CornerBits`, `BlockConstants`.
- `Core/Blocks/CornerShapes.cs`: the 256-entry shape table (convex hull of the remaining corners), face coverage for culling, `BlockFaces`.
- `Core/Blocks/BlockWorld.cs` + `ChunkData.cs`: chunked storage, dirty-chunk tracking, sanitizing (unknown materials and flat shapes become air).
- `Core/Blocks/BlockEdit.cs`: edit commands (`Fill` box, `Chisel` a cube region's corners) and `BlockEditor` (validate + deterministic apply, `RegionCorners`).
- `Core/Blocks/WorldSaveFormat.cs`: the world file (seed + chunks).
- `Core/Terrain/TerrainGenerator.cs`: seed to height, forest amount, steepness, tree spawns. `TerrainSettings` holds sizes.
- `Core/Terrain/TerrainMesher.cs`: surface nets for one 16 m chunk.
- `Core/Blocks/ChunkSerializer.cs`: run-length encoding for snapshots (and later saves).
- `Core/Blocks/ShapeStamp.cs`: build shaped blocks from an "is this point inside?" rule (slopes, pyramids, rocks). `DemoStructures.cs` uses it.
- `Core/Meshing/ChunkMesher.cs`: chunk to triangles, flat-shaded, palette UVs. Plain C# for now; Burst + greedy merging later.
- `Net/BlockWorldSync.cs`: host-authoritative edits, undo, the world seed, join snapshots, and falling pieces (detach / state / remove messages) over Netcode named messages. `Stopping` event fires before the world is cleared (save hook).
- `Game/Blocks/BlockWorldView.cs`: owns the world on this machine: loads/saves when hosting, raises `WorldStarted(seed)` / `WorldEnded`, rebuilds block chunk meshes/colliders. `BlockPalette.cs` makes the color texture.
- `Game/Persistence/WorldStorage.cs`: save file location, safe save/load, `WorldOptions` (new world vs continue).
- `Game/Terrain/TerrainView.cs`: streams terrain columns (8 columns = 128 m radius) around the camera within a per-frame time budget, trees per column, grass. `TreeMeshBuilder`, `GrassField`, `NaturePalettes` support it.
- `Game/Blocks/BlockInteractor.cs`: aiming, drag-to-build/remove with height (C), preview outline, chisel, materials, brush sizes, undo key, HUD (on the player prefab). Runs before `PlayerController` (`DefaultExecutionOrder(-50)`) so Escape can cancel a drag without freeing the mouse.
- `Core/Blocks/UndoHistory.cs`: per-player undo stacks kept by the host.
- `Core/Blocks/StructureCheck.cs`: finds groups of blocks that lost their connection to the ground.
- `Core/Blocks/BlockPiece.cs`: a loose group of blocks (extract from the world, mass, collision boxes, network encoding). `BlockPieceState` = position + rotation.
- `Game/Blocks/FallingPieces.cs` + `FallingPiece.cs`: the physics objects for loose pieces (on the `BlockWorld` object). Host simulates and sends; clients follow.
- `Game/Player/PlayerAvatar.cs` + `PixelTextures.cs`: the procedural character and its pixel textures.

## How to test
1. **Build the scene:** menu **Wildwood > Build Sandbox Scene** (rebuilds `Scenes/Sandbox.unity` and `Prefabs/Player.prefab` from code).
2. **Second player:** Window > Multiplayer > Multiplayer Play Mode, tick Player 2.
3. **Play:** press Play, click **Host** (continues the saved world) or **Host new world** in one window, and **Join** in the other.
4. **Controls:** WASD move, mouse look, Space jump, Shift sprint. Esc frees the mouse; click to capture it again.
   Building: hold right click and drag to draw a line/rectangle on the surface you started on; press C mid-drag to pull it up into a box (crosshair sets the height); release to place; Esc or the other mouse button cancels. Hold left click to do the same for removing. 1-8 material, scroll brush size (1/2/4/8 blocks, drags snap to the brush grid). C (not dragging) toggles chisel: left click deletes the marked corner of the brush box. Z undoes your last edit. V toggles first/third person.
   Note: in the Unity Editor, Escape always frees the mouse too (editor behavior); in a built game it only cancels the drag.
   Players hover in place if no ground exists below them yet (terrain still loading).
   Breaking off: build a pillar with a beam sticking out, then remove a block low in the pillar; everything above the cut falls. Left click a fallen piece to clear it.
5. **Tests:** Window > General > Test Runner > EditMode > Run All.

## Build order
1. ~~Setup pass~~ (done)
2. ~~Two players walking around a shared test scene~~ (done)
3. ~~**Blocks:** chunk data, corner-shape table, meshing, place/remove/chisel synced between players, join snapshots~~ (done)
3b. **Terrain and nature:** plains/forest terrain, trees, grass, big chisel, world saving, golden-hour look (built, needs testing)
4. **Building tools:** ~~line, rectangle, box (drag + C for height), plane lock, brush sizes, preview, undo, first/third person~~ (built, needs testing). Still to do: mirror, blueprints, auto-slope, redo.
5. ~~**Breaking off:** disconnected pieces fall with physics~~ (built, needs testing). Also: the player character.
6. **Shatter, pickups, vacuum**
7. **Weight and strength,** stress view, collapse warnings

After that: terrain digging (density edits, synced like block edits), choppable trees, more biomes, art pass (stylized shaders, grass wind, light shafts), survival loop (crafting, inventory, progression), and performance work (Burst jobs for meshing, terrain LOD).

## Project settings worth knowing
- Run In Background is on (needed for two windows), and the game starts windowed at 1280x720 for testing.
- Input uses the project-wide actions in `Assets/InputSystem_Actions.inputactions`.
- Removed packages: Visual Scripting, Unity Version Control (we use git + GitHub, with Git LFS for art and audio).
