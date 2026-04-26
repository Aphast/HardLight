# Sector Worldgen Rewrite

## Goal

Replace separate per-expedition map spawning with a single seeded sector model that:

- builds explorable space from deterministic noise and carvers,
- keeps negative space usable for ship travel and construction,
- streams chunks in and out around players,
- preserves discovered and modified areas server-side,
- spawns expeditions and dungeons inside seeded planetary regions instead of on isolated maps.

## Existing Systems To Reuse

The current codebase already has the core of a streaming world pipeline:

- `WorldControllerSystem` creates and loads chunk entities around players and `WorldLoaderComponent` entities.
- `BiomeSelectionSystem` selects chunk behavior from noise channels.
- `DebrisFeaturePlacerSystem` progressively populates chunks as they load.
- `LocalityLoaderSystem` delays structure activation until nearby chunks are actually loaded.
- `RoundPersistenceSystem` already stores expedition-related state and can be extended for sector discovery and chunk deltas.

The current salvage path is still separate-map based:

- `SpawnSalvageMissionJob` creates a new map for each expedition.
- `DungeonSystem` generates a dungeon directly into that map.

That job should become a consumer of sector coordinates, not the owner of procedural map creation.

## Recommended Architecture

### 1. Sector root state

Add a server-side sector authority for each world map.

- `SectorWorldComponent`
  - `UniverseSeed`
  - `ChunkSize`
  - `PlanetRegions`
  - `GeneratedChunks`
  - `DirtyChunks`
- `SectorWorldSystem`
  - generates the root seed on server start,
  - creates stable planet descriptors on startup,
  - answers queries for chunk content, planet conditions, and valid landing zones.

This should sit on the same world map that already uses `WorldControllerComponent`.

### 2. Planet descriptors instead of ad hoc expedition maps

Each planet should be a deterministic descriptor, not a separate always-loaded map.

- `PlanetDescriptor`
  - `PlanetId`
  - `DisplayName`
  - `PlanetSeed`
  - `Bounds`
  - `PrimaryBiome`
  - `Temperature`
  - `Atmosphere`
  - `TimeOfDay`
  - `WeatherBands`

Generate all planet descriptors at server startup from `UniverseSeed`.
Expeditions then target a descriptor plus a coordinate inside that descriptor.

### 3. Chunk pipeline

Replace debris-only chunk population with a layered chunk generation pass:

1. Evaluate macro noise for region ownership.
2. Evaluate density and connectivity noise.
3. Run a carver pass to form continuous asteroid belts, caverns, void lanes, and approach corridors.
4. Materialize tiles, anchored rocks, hazards, and landmarks for that chunk.
5. Apply saved deltas from persistence.

Suggested chunk stages:

- `EmptySpace`
- `RegionResolved`
- `BaseGeometryGenerated`
- `StructuresPlaced`
- `PersistenceApplied`

### 4. Carver model

Do not treat each asteroid as a separate procgen result.
Instead, build chunk geometry from a signed-density field:

- positive density = solid asteroid mass,
- near-zero density = edge band,
- negative density = traversable space.

Recommended inputs:

- continent noise for large asteroid landmasses,
- ridge noise for belts and spines,
- warp noise to break grid regularity,
- corridor noise to guarantee flyable paths,
- exclusion masks for POIs, dungeons, and player-built protected areas.

The current `NoiseRangeCarverSystem` and distance-based carvers can remain as secondary filters, but the new primary carver should operate on chunk geometry rather than point-cancelling debris spawns.

### 5. Persistence model

Do not save full maps for the sector.
Save chunk deltas.

Each persisted chunk record should contain only what differs from deterministic generation:

- removed generated entities,
- spawned player entities,
- tile changes,
- anchored structure changes,
- dungeon placements,
- discovery metadata,
- selected landing zones.

Recommended file layout:

- `data/sector/<universe-seed>/world.json`
- `data/sector/<universe-seed>/chunks/<x>_<y>.yml`
- `data/sector/<universe-seed>/planets/<planet-id>.json`

### 6. Expeditions as sector placements

Expeditions should become placements inside a planet region.

Flow:

1. Player picks a planet.
2. Server resolves visible and already-discovered landing zones.
3. If a new mission is needed, the server finds an unoccupied valid area inside the planet bounds.
4. `DungeonSystem` generates into a chunk-backed sector location instead of a separate map.
5. The placement is recorded as a persistent region reservation.

This preserves concurrent expeditions and allows the map to become progressively discovered.

### 7. UI map support

A landing-zone UI should read from server-side sector discovery, not client-side scans.

The UI state should expose:

- planet list,
- discovered regions,
- blocked regions,
- active expeditions,
- recommended landing zones,
- atmospheric and thermal warnings.

## Migration Plan

### Phase 1: deterministic seed foundation

- ensure world chunk noise is reproducible,
- add map-level world seed,
- audit every worldgen system that currently uses ad hoc random seeds.

### Phase 2: sector authority

- add `SectorWorldComponent` and `SectorWorldSystem`,
- create startup planet descriptors,
- expose APIs for chunk queries and planet metadata.

### Phase 3: chunk geometry generation

- add a geometry-first chunk generator,
- stop using debris placement as the primary asteroid model,
- store generated chunk metadata and apply chunk deltas.

### Phase 4: expedition migration

- change `SpawnSalvageMissionJob` to request a sector placement,
- generate dungeons into the live sector,
- persist reservations and expedition footprints.

### Phase 5: discovery and landing UI

- track explored chunks per planet,
- expose landing-zone selection,
- show persistent expedition markers and discovered sites.

## First Implementation Targets

The safest next code changes are:

1. Add `SectorWorldComponent` to worldgen maps.
2. Add a `SectorWorldSystem` that creates planet descriptors on startup.
3. Extend chunk load events so a sector generator can populate chunk geometry before debris placement.
4. Move salvage mission destination selection from `SpawnSalvageMissionJob` into `SectorWorldSystem`.

## Notes

- The existing world chunk controller is the correct base abstraction. Reuse it.
- The existing separate expedition map flow should be treated as a compatibility layer and removed last.
- Full-map saves for an infinite world will become too expensive. Save deltas only.
- Deterministic noise and stable seeds are non-negotiable for chunk unload/reload and server restarts.