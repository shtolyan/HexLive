using System;
using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Content;
using HexLive.UnityPresentation.Environment;
using HexLive.UnityPresentation.Input;
using HexLive.UnityPresentation.Spatial;
using UnityEngine;

namespace HexLive.UnityPresentation.Rendering
{
    // §176: a small, disconnected interior drawn in the same world as the island.
    public sealed class ModernApartmentView : MonoBehaviour
    {
        private static readonly TileCoord[] Neighbors =
        {
            new(1, 0), new(1, -1), new(0, -1), new(-1, 0), new(-1, 1), new(0, 1),
        };
        private static readonly string[] Palettes = { "cream", "blue", "green" };
        private static readonly string[] WallpaperMaterials = { "ApartmentCream", "ApartmentBlue", "ApartmentGreen" };
        private const string OpeningId = "architecture.modern_opening";
        private const string ExitJambId = "architecture.modern_exit_jamb";
        private const string WindowId = "architecture.modern_window";
        private const string WindowJambId = "architecture.modern_window_jamb";
        internal const float FloorLift = .012f;
        private const float WallHalfPanelOffset = .02375f;

        private readonly List<(GameObject wall, Vector3 outward)> _outerWalls = new();
        private GameObject _room;
        private int _seed;
        private string _visit;
        private int _exitId;

        public void Sync(WorldSnapshot snapshot, Func<TileCoord, float> groundHeight)
        {
            var exit = snapshot.Objects.Find(obj => obj.DefinitionId == PortalWorld.DoorDefinitionId &&
                PortalWorld.IsApartmentTile(obj.Tile));
            if (exit == null)
            {
                ClearRoom();
                return;
            }
            if (_room != null && _seed == snapshot.Seed && _visit == exit.Variant && _exitId == exit.Id.Value) return;

            var prefabs = new Dictionary<string, GameObject>();
            foreach (string palette in Palettes)
            {
                if (!Load("architecture.modern_floor_" + palette) ||
                    !Load("architecture.modern_wall_" + palette)) return;
            }
            if (!Load(OpeningId) || !Load(ExitJambId) || !Load(WindowId) || !Load(WindowJambId)) return;

            ClearRoom();
            _seed = snapshot.Seed;
            _visit = exit.Variant;
            _exitId = exit.Id.Value;
            int serial = PortalWorld.DoorVisitSerial(_visit);
            int.TryParse(_visit, out int legacyDay);
            int paletteSeed = serial > 0 ? serial : legacyDay;
            bool hasExitEdge = PortalWorld.TryGetReturnDoorEdge(exit.Tile, _seed, out var exitNeighbor, serial);
            _room = new GameObject("Modern apartment");
            _room.transform.SetParent(transform, false);
            var tiles = PortalWorld.ApartmentTiles(_seed, serial);
            var layout = new HashSet<TileCoord>(tiles);
            var litRooms = new HashSet<PortalApartmentRoom>();
            foreach (TileCoord tile in tiles)
            {
                var room = PortalWorld.RoomAt(tile, _seed, serial);
                int palette = PortalWorld.WallpaperIndex(_seed, paletteSeed, room);
                Vector3 center = SimulationUnityMapper.ToUnityTilePosition(tile, groundHeight(tile));
                Place(prefabs["architecture.modern_floor_" + Palettes[palette]],
                    center + Vector3.up * FloorLift, Quaternion.identity);
                if (litRooms.Add(room)) AddRoomLight(center);

                foreach (TileCoord delta in Neighbors)
                {
                    var neighbor = new TileCoord(tile.Q + delta.Q, tile.R + delta.R);
                    bool exterior = !layout.Contains(neighbor);
                    var otherRoom = PortalWorld.RoomAt(neighbor, _seed, serial);
                    if (otherRoom == room) continue;
                    if (!exterior && (tile.Q > neighbor.Q || tile.Q == neighbor.Q && tile.R > neighbor.R)) continue;
                    var (midpoint, rotation) = EdgePose(tile, neighbor, center.y);
                    var outward = rotation * Vector3.forward;
                    var innerWall = prefabs["architecture.modern_wall_" + Palettes[palette]];
                    if (exterior)
                    {
                        if (hasExitEdge && tile == exit.Tile && neighbor == exitNeighbor)
                        {
                            var jamb = Place(prefabs[ExitJambId], midpoint, rotation);
                            ApplyWallpaper(WallpaperShell(jamb), innerWall, palette);
                            continue;
                        }
                        if (PortalWorld.TryGetWindowEdge(tile, _seed, exit.Tile, out var windowNeighbor, serial) &&
                            neighbor == windowNeighbor)
                        {
                            var jamb = Place(prefabs[WindowJambId], midpoint, rotation);
                            ApplyWallpaper(WallpaperShell(jamb), innerWall, palette);
                            // The view and frame sit on the wall centreline inside the physical aperture.
                            var window = Place(prefabs[WindowId], midpoint,
                                Quaternion.LookRotation(-outward, Vector3.up));
                            window.transform.SetParent(jamb.transform, true);
                            _outerWalls.Add((jamb, outward));
                        }
                        else _outerWalls.Add((Place(innerWall, midpoint, rotation), outward));
                        continue;
                    }

                    int otherPalette = PortalWorld.WallpaperIndex(_seed, paletteSeed, otherRoom);
                    var outerWall = prefabs["architecture.modern_wall_" + Palettes[otherPalette]];
                    if (PortalWorld.HasWall(tile, neighbor, _seed, serial))
                    {
                        PlaceHalf(innerWall, midpoint - outward * WallHalfPanelOffset, rotation);
                        PlaceHalf(outerWall, midpoint + outward * WallHalfPanelOffset, rotation);
                        continue;
                    }

                    var doorway = Place(prefabs[OpeningId], midpoint, rotation);
                    SetOpeningFaces(doorway, outward, innerWall, palette, outerWall, otherPalette);
                }
            }

            bool Load(string id)
            {
                var prefab = LoadPrefab(id);
                if (prefab == null) return false;
                prefabs.Add(id, prefab);
                return true;
            }
        }

        private void LateUpdate()
        {
            if (_room == null || Camera.main is not { } camera) return;
            var rig = camera.GetComponent<RtsCameraController>();
            bool visible = rig != null && rig.IsViewingModernApartment;
            if (_room.activeSelf != visible) _room.SetActive(visible);
            if (!visible) return;

            var viewer = camera.transform.position;
            foreach (var (wall, outward) in _outerWalls)
            {
                // Cut away the near perimeter from the current orbit angle.
                wall.SetActive(Vector3.Dot(viewer - wall.transform.position, outward) <= .05f);
            }
        }

        private GameObject Place(GameObject prefab, Vector3 position, Quaternion rotation)
        {
            // The placement root owns the hex pose. The child retains the FBX import basis.
            var root = new GameObject(prefab.name);
            root.transform.SetParent(_room.transform, false);
            root.transform.SetPositionAndRotation(position, rotation);
            Instantiate(prefab, root.transform, false);
            return root;
        }

        private Transform AddHalfPanel(string name, Vector3 position, Quaternion rotation)
        {
            var half = new GameObject(name).transform;
            half.SetParent(_room.transform, false);
            half.SetPositionAndRotation(position, rotation);
            half.localScale = new Vector3(1f, 1f, .5f);
            return half;
        }

        private void PlaceHalf(GameObject prefab, Vector3 position, Quaternion rotation)
        {
            // Each room's own pattern remains visible on its side of the partition.
            Instantiate(prefab, AddHalfPanel("Wallpaper half panel", position, rotation), false);
        }

        private void SetOpeningFaces(GameObject doorway, Vector3 outward, GameObject innerWall, int innerPalette,
            GameObject outerWall, int outerPalette)
        {
            var shell = WallpaperShell(doorway);
            if (shell == null) return;

            // The trim and pocket leaf stay intact. Only the wall filler is doubled.
            for (int side = -1; side <= 1; side += 2)
            {
                var half = AddHalfPanel("Doorway wallpaper half panel",
                    doorway.transform.position + side * outward * WallHalfPanelOffset, doorway.transform.rotation);
                var panel = Instantiate(shell.gameObject, half, false);
                panel.transform.SetLocalPositionAndRotation(
                    doorway.transform.InverseTransformPoint(shell.transform.position),
                    Quaternion.Inverse(doorway.transform.rotation) * shell.transform.rotation);
                ApplyWallpaper(panel.GetComponent<MeshRenderer>(), side < 0 ? innerWall : outerWall,
                    side < 0 ? innerPalette : outerPalette);
            }
            shell.enabled = false;
        }

        private static MeshRenderer WallpaperShell(GameObject module)
        {
            foreach (var renderer in module.GetComponentsInChildren<MeshRenderer>(true))
                if (renderer.name == "wall_shell") return renderer;
            return null;
        }

        private static void ApplyWallpaper(MeshRenderer shell, GameObject wall, int palette)
        {
            if (shell == null || wall.GetComponentInChildren<MeshRenderer>(true) is not { } wallRenderer) return;
            foreach (var material in wallRenderer.sharedMaterials)
            {
                if (material == null || material.name != WallpaperMaterials[palette]) continue;
                shell.sharedMaterial = material;
                return;
            }
        }

        private static (Vector3 Position, Quaternion Rotation) EdgePose(TileCoord tile, TileCoord neighbor, float y)
        {
            var center = SimulationUnityMapper.ToUnityTilePosition(tile, y);
            var next = SimulationUnityMapper.ToUnityTilePosition(neighbor, y);
            return ((center + next) * .5f + Vector3.up * FloorLift,
                Quaternion.LookRotation(next - center, Vector3.up));
        }

        internal static bool TryGetReturnDoorPose(TileCoord tile, int seed, float y, out Vector3 position,
            out Quaternion rotation, int visitSerial = 0)
        {
            if (PortalWorld.TryGetReturnDoorEdge(tile, seed, out var neighbor, visitSerial))
            {
                (position, rotation) = EdgePose(tile, neighbor, y);
                return true;
            }
            position = default;
            rotation = Quaternion.identity;
            return false;
        }

        private void AddRoomLight(Vector3 center)
        {
            var light = new GameObject("Apartment ceiling light").AddComponent<Light>();
            light.transform.SetParent(_room.transform, false);
            light.transform.position = center + Vector3.up * 2.1f;
            light.type = LightType.Point;
            light.color = new Color(1f, .88f, .71f);
            light.range = 5f;
            light.intensity = 3f;
            light.shadows = LightShadows.None;
        }

        private void ClearRoom()
        {
            if (_room != null)
            {
                if (Application.isPlaying) Destroy(_room);
                else DestroyImmediate(_room);
            }
            _room = null;
            _outerWalls.Clear();
        }

        private void OnDisable() => ClearRoom();

        public static GameObject LoadPrefab(string id)
        {
#if UNITY_EDITOR
            // Local portal experiments can use checked-in art before publishing
            // their independent object owners. Remote sessions use the registry.
            if (SimulationSource.Current?.Link.IsRemote != true)
            {
                var local = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                    $"Assets/HexLiveContent/RuntimeSource/Objects/{id}.fbx");
                if (local != null) return local;
            }
#endif
            return PortalContentPolicy.IsApproved("building", id)
                ? ContentPrefabCache.GetOrRequest("building", id)
                : WorldPropResources.Load(id);
        }
    }

    internal sealed class ModernPortalGlow : MonoBehaviour
    {
        private Material _material;

        private void Awake()
        {
            foreach (var renderer in GetComponentsInChildren<MeshRenderer>(true))
            {
                if (renderer.name != "door_glow" || renderer.sharedMaterial == null) continue;
                _material = new Material(renderer.sharedMaterial) { name = "Portal seam", hideFlags = HideFlags.DontSave };
                _material.EnableKeyword("_EMISSION");
                _material.SetColor("_EmissionColor", new Color(.28f, .2f, .12f));
                renderer.sharedMaterial = _material;
                break;
            }
        }

        private void OnDestroy()
        {
            if (_material != null) Destroy(_material);
        }
    }
}

