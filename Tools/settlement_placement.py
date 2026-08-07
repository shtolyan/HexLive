#!/usr/bin/env python3
"""Pure-Python prototype for deterministic HexLive settlement placement.

The module deliberately has no Blender or Unity dependencies.  The Blender
visualizer imports it, while a future simulation port can keep the same data
flow: generate terrain occupancy, score blueprint transforms, validate an
entrance path, commit the winning footprint, repeat.
"""

from __future__ import annotations

from collections import deque
from dataclasses import dataclass
import json
import math
import random
from typing import Iterable


Hex = tuple[int, int]

DIRECTIONS: tuple[Hex, ...] = (
    (1, 0),
    (0, 1),
    (-1, 1),
    (-1, 0),
    (0, -1),
    (1, -1),
)


@dataclass(frozen=True)
class HouseDefinition:
    definition_id: str
    offsets: tuple[Hex, ...]
    preferred_radius: float


@dataclass
class GeneratedMap:
    radius: int
    campfire: Hex
    terrain: dict[Hex, str]
    props: dict[Hex, str]

    @property
    def cells(self) -> set[Hex]:
        return set(self.terrain)

    @property
    def water(self) -> set[Hex]:
        return {cell for cell, kind in self.terrain.items() if kind == "water"}

    @property
    def blocked_props(self) -> set[Hex]:
        return set(self.props)


@dataclass
class Placement:
    building_id: int
    definition_id: str
    anchor: Hex
    rotation: int
    footprint: tuple[Hex, ...]
    door_cell: Hex
    door_outside: Hex
    path_to_campfire: tuple[Hex, ...]
    score: float


HOUSE_CATALOG: dict[str, HouseDefinition] = {
    "hut_1hex": HouseDefinition("hut_1hex", ((0, 0),), 3.2),
    "longhouse_2hex": HouseDefinition("longhouse_2hex", ((0, 0), (1, 0)), 4.0),
    "bend_3hex": HouseDefinition("bend_3hex", ((0, 0), (1, 0), (0, 1)), 4.8),
    "great_house_7hex": HouseDefinition(
        "great_house_7hex",
        ((0, 0), (1, 0), (0, 1), (-1, 1), (-1, 0), (0, -1), (1, -1)),
        4.0,
    ),
    "wing_house_5hex": HouseDefinition(
        "wing_house_5hex",
        ((0, 0), (1, 0), (2, 0), (0, 1), (1, 1)),
        4.2,
    ),
}


def add(a: Hex, b: Hex) -> Hex:
    return a[0] + b[0], a[1] + b[1]


def subtract(a: Hex, b: Hex) -> Hex:
    return a[0] - b[0], a[1] - b[1]


def distance(a: Hex, b: Hex = (0, 0)) -> int:
    dq = a[0] - b[0]
    dr = a[1] - b[1]
    return (abs(dq) + abs(dr) + abs(dq + dr)) // 2


def neighbors(cell: Hex) -> tuple[Hex, ...]:
    return tuple(add(cell, direction) for direction in DIRECTIONS)


def rotate(cell: Hex, turns: int) -> Hex:
    q, r = cell
    for _ in range(turns % 6):
        q, r = -r, q + r
    return q, r


def cells_in_radius(radius: int) -> list[Hex]:
    result = []
    for q in range(-radius, radius + 1):
        r_min = max(-radius, -q - radius)
        r_max = min(radius, -q + radius)
        for r in range(r_min, r_max + 1):
            result.append((q, r))
    return result


def expanded(cells: Iterable[Hex], rings: int = 1) -> set[Hex]:
    result = set(cells)
    frontier = set(cells)
    for _ in range(rings):
        frontier = {neighbor for cell in frontier for neighbor in neighbors(cell)} - result
        result.update(frontier)
    return result


def generate_map(seed: int, radius: int = 7) -> GeneratedMap:
    """Create a dry island with two edge ponds and deterministic trees/rocks."""

    rng = random.Random(seed)
    all_cells = cells_in_radius(radius)
    terrain = {cell: "ground" for cell in all_cells}

    pond_centers = ((-radius + 1, 2), (radius - 2, -radius + 2))
    water = set()
    for center in pond_centers:
        water.update(cell for cell in all_cells if distance(cell, center) <= 1)
    for cell in all_cells:
        if distance(cell) == radius and ((cell[0] + 2 * cell[1] + seed) % 7 == 0):
            water.add(cell)
    for cell in water:
        terrain[cell] = "water"

    protected_center = expanded({(0, 0)}, 2)
    candidates = [cell for cell in all_cells if cell not in water and cell not in protected_center]
    rng.shuffle(candidates)
    props: dict[Hex, str] = {}
    tree_target = max(18, radius * 4)
    rock_target = max(9, radius * 2)
    for cell in candidates:
        if len([kind for kind in props.values() if kind == "tree"]) < tree_target:
            props[cell] = "tree"
        elif len([kind for kind in props.values() if kind == "rock"]) < rock_target:
            props[cell] = "rock"
        if len(props) >= tree_target + rock_target:
            break

    return GeneratedMap(radius=radius, campfire=(0, 0), terrain=terrain, props=props)


def transformed_footprint(definition: HouseDefinition, anchor: Hex, rotation: int) -> tuple[Hex, ...]:
    return tuple(sorted(add(anchor, rotate(offset, rotation)) for offset in definition.offsets))


def find_door(
    footprint: set[Hex],
    generated_map: GeneratedMap,
    occupied: set[Hex],
) -> tuple[Hex, Hex] | None:
    options: list[tuple[float, Hex, Hex]] = []
    forbidden = generated_map.water | generated_map.blocked_props | occupied | footprint
    for cell in footprint:
        for outside in neighbors(cell):
            if outside in footprint or outside not in generated_map.cells or outside in forbidden:
                continue
            toward_fire = distance(outside, generated_map.campfire)
            options.append((toward_fire + distance(cell, generated_map.campfire) * 0.05, cell, outside))
    if not options:
        return None
    _, door_cell, outside = min(options, key=lambda option: (option[0], option[1], option[2]))
    return door_cell, outside


def find_path(
    start: Hex,
    goal: Hex,
    generated_map: GeneratedMap,
    blocked: set[Hex],
) -> tuple[Hex, ...] | None:
    forbidden = generated_map.water | generated_map.blocked_props | blocked
    frontier = deque([start])
    came_from: dict[Hex, Hex | None] = {start: None}
    while frontier:
        current = frontier.popleft()
        if current == goal:
            break
        ordered_neighbors = sorted(neighbors(current), key=lambda cell: (distance(cell, goal), cell))
        for next_cell in ordered_neighbors:
            if next_cell not in generated_map.cells or next_cell in came_from:
                continue
            if next_cell in forbidden and next_cell != goal:
                continue
            came_from[next_cell] = current
            frontier.append(next_cell)
    if goal not in came_from:
        return None
    result = []
    current: Hex | None = goal
    while current is not None:
        result.append(current)
        current = came_from[current]
    result.reverse()
    return tuple(result)


def centroid_angle(cells: Iterable[Hex]) -> float:
    cells = tuple(cells)
    q = sum(cell[0] + cell[1] * 0.5 for cell in cells) / len(cells)
    y = sum(cell[1] for cell in cells) / len(cells)
    return math.atan2(y, q)


def angular_separation(a: float, b: float) -> float:
    delta = abs(a - b) % (math.pi * 2.0)
    return min(delta, math.pi * 2.0 - delta)


def stable_tiebreak(seed: int, anchor: Hex, rotation: int, building_id: int) -> float:
    value = seed * 73856093
    value ^= anchor[0] * 19349663
    value ^= anchor[1] * 83492791
    value ^= rotation * 2654435761
    value ^= building_id * 97531
    return float(value & 0xFFFF) / 65535.0


def place_settlement(
    generated_map: GeneratedMap,
    requests: Iterable[str],
    seed: int,
) -> list[Placement]:
    """Place blueprints outward from the fire without clearing natural props."""

    placements: list[Placement] = []
    occupied: set[Hex] = {generated_map.campfire}
    reserved_for_buildings: set[Hex] = expanded({generated_map.campfire}, 1)
    used_angles: list[float] = []

    anchors = sorted(
        (cell for cell in generated_map.cells if generated_map.terrain[cell] == "ground"),
        key=lambda cell: (distance(cell, generated_map.campfire), cell),
    )

    for building_id, definition_id in enumerate(requests, start=1):
        definition = HOUSE_CATALOG[definition_id]
        best: Placement | None = None
        for anchor in anchors:
            for rotation in range(6):
                footprint_tuple = transformed_footprint(definition, anchor, rotation)
                footprint = set(footprint_tuple)
                if any(cell not in generated_map.cells for cell in footprint):
                    continue
                if any(generated_map.terrain[cell] != "ground" for cell in footprint):
                    continue
                if footprint & (generated_map.blocked_props | occupied | reserved_for_buildings):
                    continue

                door = find_door(footprint, generated_map, occupied)
                if door is None:
                    continue
                door_cell, door_outside = door
                path = find_path(
                    door_outside,
                    generated_map.campfire,
                    generated_map,
                    blocked=occupied | footprint,
                )
                if path is None:
                    continue

                mean_radius = sum(distance(cell, generated_map.campfire) for cell in footprint) / len(footprint)
                angle = centroid_angle(footprint)
                radius_penalty = abs(mean_radius - definition.preferred_radius) * 9.0
                path_penalty = len(path) * 0.18
                prop_neighbors = expanded(footprint, 1) & generated_map.blocked_props
                crowd_penalty = len(prop_neighbors) * 0.42
                spread_penalty = 0.0
                for used_angle in used_angles:
                    separation = angular_separation(angle, used_angle)
                    if separation < math.radians(55.0):
                        spread_penalty += (math.radians(55.0) - separation) * 7.5
                score = (
                    radius_penalty
                    + path_penalty
                    + crowd_penalty
                    + spread_penalty
                    + stable_tiebreak(seed, anchor, rotation, building_id) * 0.08
                )
                candidate = Placement(
                    building_id=building_id,
                    definition_id=definition_id,
                    anchor=anchor,
                    rotation=rotation,
                    footprint=footprint_tuple,
                    door_cell=door_cell,
                    door_outside=door_outside,
                    path_to_campfire=path,
                    score=round(score, 5),
                )
                if best is None or (candidate.score, candidate.anchor, candidate.rotation) < (best.score, best.anchor, best.rotation):
                    best = candidate

        if best is None:
            raise RuntimeError(f"No valid placement for {definition_id} #{building_id}")
        placements.append(best)
        footprint = set(best.footprint)
        occupied.update(footprint)
        reserved_for_buildings.update(expanded(footprint, 1))
        used_angles.append(centroid_angle(footprint))

    errors = validate_placements(generated_map, placements)
    if errors:
        raise RuntimeError("Invalid settlement placement: " + "; ".join(errors))
    return placements


def validate_placements(generated_map: GeneratedMap, placements: Iterable[Placement]) -> list[str]:
    placements = tuple(placements)
    errors: list[str] = []
    all_footprints: set[Hex] = set()
    for placement in placements:
        footprint = set(placement.footprint)
        overlap = footprint & all_footprints
        if overlap:
            errors.append(f"building {placement.building_id} overlaps {sorted(overlap)}")
        all_footprints.update(footprint)
        blocked = footprint & (generated_map.water | generated_map.blocked_props | {generated_map.campfire})
        if blocked:
            errors.append(f"building {placement.building_id} occupies blocked cells {sorted(blocked)}")
        if placement.door_outside in footprint:
            errors.append(f"building {placement.building_id} door exits into its own footprint")
    footprint_union = set().union(*(set(placement.footprint) for placement in placements)) if placements else set()
    for placement in placements:
        path = placement.path_to_campfire
        if not path or path[0] != placement.door_outside or path[-1] != generated_map.campfire:
            errors.append(f"building {placement.building_id} has incomplete entrance path")
            continue
        for start, end in zip(path, path[1:]):
            if end not in neighbors(start):
                errors.append(f"building {placement.building_id} path jumps from {start} to {end}")
        illegal_path = set(path[:-1]) & (generated_map.water | generated_map.blocked_props | footprint_union)
        if illegal_path:
            errors.append(f"building {placement.building_id} path crosses blocked cells {sorted(illegal_path)}")
    return errors


def as_dict(generated_map: GeneratedMap, placements: Iterable[Placement]) -> dict[str, object]:
    return {
        "radius": generated_map.radius,
        "campfire": generated_map.campfire,
        "water": sorted(generated_map.water),
        "props": [{"cell": cell, "kind": kind} for cell, kind in sorted(generated_map.props.items())],
        "placements": [
            {
                "buildingId": placement.building_id,
                "definitionId": placement.definition_id,
                "anchor": placement.anchor,
                "rotation": placement.rotation,
                "footprint": placement.footprint,
                "doorCell": placement.door_cell,
                "doorOutside": placement.door_outside,
                "pathToCampfire": placement.path_to_campfire,
                "score": placement.score,
            }
            for placement in placements
        ],
    }


def main() -> None:
    seed = 7319
    generated_map = generate_map(seed)
    requests = ("great_house_7hex", "wing_house_5hex")
    placements = place_settlement(generated_map, requests, seed)
    print(json.dumps(as_dict(generated_map, placements), indent=2))


if __name__ == "__main__":
    main()
