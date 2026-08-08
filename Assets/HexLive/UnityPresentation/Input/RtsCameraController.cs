using System.Collections.Generic;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.Spatial;
using UnityEngine;
using UnityEngine.InputSystem;
using HexLive.Simulation.Common;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Spatial;

namespace HexLive.UnityPresentation.Input
{
    /// <summary>
    /// RTS-style camera built as a single orbit rig around a pivot. Only what
    /// the pivot is attached to changes:
    ///   • Free: the pivot is a loose point magnetised to the hex ground;
    ///     WASD/arrows slide it, right-drag rotates, scroll zooms.
    ///   • Orbit: click an NPC and the pivot follows their body exactly;
    ///     smoothing delays the catch-up but never changes its target. Escape
    ///     releases it where it settled — the angle,
    ///     distance and framing stay put instead of snapping back to a top-down
    ///     view.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RtsCameraController : MonoBehaviour
    {
        [Header("Runner")]
        [SerializeField] private SimulationRunnerBehaviour _runner;

        [Header("Start")]
        [SerializeField] private Vector3 _startPosition = new(4.3f, 9f, 3.36f);
        [SerializeField] private Vector3 _startRotation = new(90f, -180f, 0f);

        [Header("Pan")]
        [SerializeField] private float _panSpeed = 6f;
        [SerializeField] private float _panSmooth = 0.1f;

        [Header("Zoom")]
        [SerializeField] private float _minHeight = 3f;
        [SerializeField] private float _maxHeight = 20f;
        [SerializeField] private float _zoomSpeed = 1.5f;

        [Tooltip("Free-mode pitch ceiling. 90 = straight down (the classic RTS top view).")]
        [SerializeField] private float _freeMaxPitch = 90f;

        [Header("Orbit")]
        [SerializeField] private float _orbitDistance = 9f;
        [SerializeField] private float _orbitMinDistance = 0.7f; // close-up: face fills the frame
        [SerializeField] private float _orbitMaxDistance = 22f;
        [SerializeField] private float _orbitZoomSpeed = 4f;
        [SerializeField] private float _orbitRotationSpeed = 0.2f;
        [SerializeField] private float _orbitPitch = 25f;
        [SerializeField] private float _orbitMinPitch = 5f;
        [SerializeField] private float _orbitMaxPitch = 80f;
        [SerializeField] private float _orbitYaw = 0f;
        [SerializeField] private float _orbitPositionSmooth = 0.18f;
        [SerializeField] private float _orbitRotationSmooth = 0.08f;
        [SerializeField] private float _pickRadiusPixels = 70f;

        [Header("Hex picking")]
        [SerializeField] private float _hexPickVerticalPadding = 0.04f;

        [Tooltip("Orbit pivot height above the NPC's feet, as a fraction of the hex radius (neck ≈ 0.62).")]
        [SerializeField] private float _orbitNeckFactor = 0.62f;

        [Tooltip("How smoothly the pivot catches up with the NPC. The NPC itself is always the target.")]
        [SerializeField] private float _orbitTargetSmooth = 0.18f;

        private enum Mode
        {
            Free,
            Orbit
        }

        private Mode _mode = Mode.Free;

        // Free-mode state: a loose pivot glued to the hex ground.
        private Vector3 _freePivot;
        private Vector3 _velocity;

        // Shared rig state (both modes drive the same yaw/pitch/distance).
        private float _currentYaw;
        private float _currentPitch;
        private float _currentDistance;
        private float _smoothedYaw;
        private float _smoothedPitch;
        private float _yawVelocity;
        private float _pitchVelocity;

        // Orbit-mode state.
        private int _orbitTargetId;
        private Vector3 _smoothedTarget;
        private Vector3 _targetVelocity;
        private bool _hasSmoothedTarget;
        private readonly List<int> _orbitRoster = new();

        private Camera _camera;
        private HexWorldRenderer _worldRenderer;

        // Ground magnet: tile top heights, refreshed periodically so the free
        // pivot can hug elevation without rebuilding a snapshot every frame.
        private readonly Dictionary<TileCoord, float> _tileTops = new();
        private float _tileTopsStamp = float.NegativeInfinity;
        private const float TileTopsRefreshSeconds = 0.5f;

        private const float ElevationStep = 0.55f;

        public void SetRunner(SimulationRunnerBehaviour runner)
        {
            _runner = runner;
        }

        /// <summary>
        /// Spec §112: what the rig is framing right now — the followed colonist
        /// while orbiting, the ground pivot while panning. The foliage culler
        /// clears the leaves standing between the lens and this point.
        /// </summary>
        public Vector3 FocusPoint =>
            _mode == Mode.Orbit && _hasSmoothedTarget ? _smoothedTarget : _freePivot;

        /// <summary>
        /// Spec §112: true while the rig is framing a PERSON (orbit mode), i.e.
        /// while <see cref="FocusPoint"/> is a subject and not just the ground
        /// under a free-flying camera. The culler needs the difference: only a
        /// subject gives the depth plane «нельзя стоять ближе неё».
        /// </summary>
        public bool HasFramedSubject => _mode == Mode.Orbit && _hasSmoothedTarget;

        private void Start()
        {
            _camera = GetComponent<Camera>();
            transform.position = _startPosition;
            transform.rotation = Quaternion.Euler(_startRotation);

            _currentYaw = _startRotation.y;
            _currentPitch = Mathf.Clamp(_startRotation.x, _orbitMinPitch, _freeMaxPitch);
            _smoothedYaw = _currentYaw;
            _smoothedPitch = _currentPitch;
            _currentDistance = Mathf.Clamp(_startPosition.y, _orbitMinDistance, _orbitMaxDistance);

            // The start pose looks straight down, so the pivot is simply the
            // ground under the camera.
            _freePivot = SnapToGround(new Vector3(_startPosition.x, 0f, _startPosition.z));
        }

        private void OnEnable()
        {
            NpcSelection.SelectionChanged += OnSelectionChanged;
        }

        private void OnDisable()
        {
            NpcSelection.SelectionChanged -= OnSelectionChanged;
        }

        // Selection is the single source of truth: clicking an NPC (or the panel
        // switching characters) drives the camera into/out of orbit.
        private void OnSelectionChanged(int npcId)
        {
            if (npcId >= 0)
            {
                if (_mode == Mode.Orbit)
                {
                    SwitchOrbitTarget(npcId);
                }
                else
                {
                    EnterOrbit(npcId);
                }
            }
            else if (_mode == Mode.Orbit)
            {
                ExitOrbit();
            }
        }

        private void LateUpdate()
        {
            if (_runner == null)
            {
                _runner = FindAnyObjectByType<SimulationRunnerBehaviour>();
            }

            if (_mode == Mode.Orbit)
            {
                UpdateOrbit();
            }
            else
            {
                UpdateFree();
            }
        }

        // ---- Free mode -------------------------------------------------------

        private void UpdateFree()
        {
            if (!UI.GameMenu.IsOpen && !UI.EndSummaryPanel.IsOpen)
            {
                HandlePan();
                HandleZoom();
                HandleFreeRotation();
                TryHandleLeftClick();
            }

            _freePivot = SnapToGround(_freePivot);

            ApplyRig(_freePivot, _panSmooth);
        }

        private void HandlePan()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            var input = Vector2.zero;

            if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) input.y += 1f;
            if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) input.y -= 1f;
            if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) input.x += 1f;
            if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) input.x -= 1f;

            if (input.sqrMagnitude < 0.001f) return;

            input = Vector2.ClampMagnitude(input, 1f);

            var heightFactor = Mathf.Lerp(0.5f, 1.5f, Mathf.InverseLerp(_minHeight, _maxHeight, _currentDistance));
            var speed = _panSpeed * heightFactor * Time.unscaledDeltaTime;

            // Slide the pivot along the camera's own ground axes so screen
            // right/up match the arrow pressed at any yaw (raw world X/Z would
            // invert as soon as the camera is turned around).
            var flat = Quaternion.Euler(0f, _smoothedYaw, 0f);
            var right = flat * Vector3.right;
            var up = flat * Vector3.forward; // screen-up projected on the ground

            var move = (right * input.x + up * input.y) * speed;
            _freePivot.x += move.x;
            _freePivot.z += move.z;
        }

        private void HandleZoom()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;

            var scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) < 0.01f) return;

            _currentDistance = Mathf.Clamp(
                _currentDistance - scroll * _zoomSpeed * 0.01f,
                _orbitMinDistance, _orbitMaxDistance);
        }

        private void HandleFreeRotation()
        {
            var mouse = Mouse.current;
            if (mouse == null || !mouse.rightButton.isPressed) return;

            var delta = mouse.delta.ReadValue();
            _currentYaw += delta.x * _orbitRotationSpeed;
            _currentPitch -= delta.y * _orbitRotationSpeed;
            _currentPitch = Mathf.Clamp(_currentPitch, _orbitMinPitch, _freeMaxPitch);
        }

        // ---- Shared rig ------------------------------------------------------

        // Places the camera on its orbit around <paramref name="pivot"/>.
        private void ApplyRig(Vector3 pivot, float positionSmooth)
        {
            _smoothedYaw = Mathf.SmoothDampAngle(
                _smoothedYaw, _currentYaw, ref _yawVelocity, _orbitRotationSmooth,
                Mathf.Infinity, Time.unscaledDeltaTime);
            _smoothedPitch = Mathf.SmoothDampAngle(
                _smoothedPitch, _currentPitch, ref _pitchVelocity, _orbitRotationSmooth,
                Mathf.Infinity, Time.unscaledDeltaTime);

            var rotation = Quaternion.Euler(_smoothedPitch, _smoothedYaw, 0f);

            // The character bar covers the bottom of the screen, so aiming the
            // pivot at the screen center hides the NPC's legs behind the UI.
            // Slide the frame down by half the bar's coverage: the pivot then
            // lands in the middle of the strip that stays visible above the bar.
            // With nothing selected the bar is hidden and the lift is zero.
            var uiLift = 0f;
            var coverage = NpcSelection.BottomUiCoverage;
            if (_camera != null && coverage > 0.001f)
            {
                var frustumHeight = 2f * _currentDistance *
                    Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
                uiLift = frustumHeight * coverage * 0.5f;
            }

            var desiredPosition = pivot
                - rotation * Vector3.forward * _currentDistance
                - rotation * Vector3.up * uiLift;

            transform.position = Vector3.SmoothDamp(
                transform.position, desiredPosition, ref _velocity, positionSmooth,
                Mathf.Infinity, Time.unscaledDeltaTime);
            transform.rotation = rotation;
        }

        // ---- Ground magnet ---------------------------------------------------

        // Drops a pivot onto the top of the hex it stands over. Water tiles
        // magnetise to their surface, so the pivot never sinks under the sea.
        private Vector3 SnapToGround(Vector3 pivot)
        {
            RefreshTileTops();
            if (_tileTops.TryGetValue(WorldToTile(pivot.x, pivot.z), out var top))
            {
                pivot.y = top;
            }

            return pivot;
        }

        private void RefreshTileTops()
        {
            if (Time.unscaledTime - _tileTopsStamp < TileTopsRefreshSeconds)
            {
                return;
            }

            var snapshot = _runner != null && _runner.IsReady ? _runner.CreateSnapshot() : null;
            if (snapshot == null || snapshot.Tiles.Count == 0)
            {
                return;
            }

            _tileTopsStamp = Time.unscaledTime;
            _tileTops.Clear();
            for (var i = 0; i < snapshot.Tiles.Count; i++)
            {
                var tile = snapshot.Tiles[i];
                _tileTops[tile.Coord] = TileTopY(tile);
            }
        }

        // Inverse of HexSpatialMath.TileToWorld plus cube rounding.
        private static TileCoord WorldToTile(float x, float z)
        {
            var r = z / (HexSpatialMath.HexRadius * HexSpatialMath.HexRowStepFactor);
            var q = x / (HexSpatialMath.HexRadius * HexSpatialMath.HexWidthFactor) - r * 0.5f;
            var s = -q - r;

            var rq = Mathf.RoundToInt(q);
            var rr = Mathf.RoundToInt(r);
            var rs = Mathf.RoundToInt(s);

            var dq = Mathf.Abs(rq - q);
            var dr = Mathf.Abs(rr - r);
            var ds = Mathf.Abs(rs - s);

            if (dq > dr && dq > ds)
            {
                rq = -rr - rs;
            }
            else if (dr > ds)
            {
                rr = -rq - rs;
            }

            return new TileCoord(rq, rr);
        }

        // Left-click first tries an NPC, then falls back to the map hex under
        // the cursor. Hex hit testing uses the sim snapshot rather than view
        // colliders so invisible anchors and changed elevation still inspect.
        private void TryHandleLeftClick(WorldSnapshot snapshot = null)
        {
            var mouse = Mouse.current;
            if (mouse == null || !mouse.leftButton.wasPressedThisFrame)
            {
                return;
            }

            // Don't pick NPCs behind the character bar or through the menu.
            if (NpcSelection.PointerOverUi || UI.HexInspectorPanel.PointerOverPanel ||
                UI.GameMenu.IsOpen || UI.EndSummaryPanel.IsOpen)
            {
                return;
            }

            if (TryPickNpc(mouse.position.ReadValue(), snapshot))
            {
                HexSelection.Clear();
                return;
            }

            if (HexSelection.Enabled && TryPickHex(mouse.position.ReadValue(), out var coord, snapshot))
            {
                HexSelection.Select(coord);
            }
        }

        // Left-click on an NPC -> enter orbit mode.
        private bool TryPickNpc(Vector2 mousePos, WorldSnapshot currentSnapshot = null)
        {
            if (_camera == null)
            {
                _camera = GetComponent<Camera>();
                if (_camera == null) return false;
            }

            var snapshot = currentSnapshot ??
                (_runner != null && _runner.IsReady ? _runner.CreateSnapshot() : null);
            if (snapshot == null || snapshot.Npcs.Count == 0)
            {
                return false;
            }

            // Pick the animated visual geometry first. The old screen-space
            // radius was centred on the feet, so a click on a head, arm or a
            // prone body failed as soon as the camera angle changed. Bounds are
            // derived from the live renderers and tested only on a click — no
            // permanent mesh colliders or per-frame skinned-mesh baking.
            if (_worldRenderer == null)
            {
                _worldRenderer = FindAnyObjectByType<HexWorldRenderer>();
            }

            if (_worldRenderer != null)
            {
                var ray = _camera.ScreenPointToRay(mousePos);
                var nearestViewDistance = float.PositiveInfinity;
                var viewHitId = -1;

                for (var i = 0; i < snapshot.Npcs.Count; i++)
                {
                    var npc = snapshot.Npcs[i];
                    if (!_worldRenderer.TryGetActorView(npc.Id.Value, out var view) ||
                        !view.TryRaycastVisibleGeometry(ray, nearestViewDistance, out var hitDistance))
                    {
                        continue;
                    }

                    nearestViewDistance = hitDistance;
                    viewHitId = npc.Id.Value;
                }

                if (viewHitId >= 0)
                {
                    NpcSelection.Select(viewHitId);
                    return true;
                }
            }

            // A just-spawned actor may not have completed its visual setup yet,
            // and prototype scenes still use primitive NPC views. Preserve the
            // point-radius fallback for those cases only.
            var bestId = -1;
            var bestDist = _pickRadiusPixels;

            foreach (var npc in snapshot.Npcs)
            {
                var world = SimulationUnityMapper.ToUnityPosition(
                    npc.Position, SimulationUnityMapper.CameraTargetHeight);
                var screen = _camera.WorldToScreenPoint(world);
                if (screen.z <= 0f)
                {
                    continue; // behind the camera
                }

                var dist = Vector2.Distance(new Vector2(screen.x, screen.y), mousePos);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestId = npc.Id.Value;
                }
            }

            if (bestId >= 0)
            {
                NpcSelection.Select(bestId);
                return true;
            }

            return false;
        }

        private bool TryPickHex(
            Vector2 mousePos,
            out HexLive.Simulation.Common.TileCoord coord,
            WorldSnapshot currentSnapshot = null)
        {
            coord = default;

            if (_camera == null)
            {
                _camera = GetComponent<Camera>();
                if (_camera == null) return false;
            }

            var snapshot = currentSnapshot ??
                (_runner != null && _runner.IsReady ? _runner.CreateSnapshot() : null);
            if (snapshot == null || snapshot.Tiles.Count == 0)
            {
                return false;
            }

            var ray = _camera.ScreenPointToRay(mousePos);
            if (Mathf.Abs(ray.direction.y) < 0.0001f)
            {
                return false;
            }

            var bestT = float.PositiveInfinity;
            var found = false;
            for (var i = 0; i < snapshot.Tiles.Count; i++)
            {
                var tile = snapshot.Tiles[i];
                var topY = TileTopY(tile);
                var t = (topY + _hexPickVerticalPadding - ray.origin.y) / ray.direction.y;
                if (t <= 0f || t >= bestT)
                {
                    continue;
                }

                var hit = ray.origin + ray.direction * t;
                if (!PointInsideHex(hit.x, hit.z, tile.Coord))
                {
                    continue;
                }

                bestT = t;
                coord = tile.Coord;
                found = true;
            }

            return found;
        }

        private static float TileTopY(TileSnapshot tile)
        {
            var y = SimulationUnityMapper.TileHeight + tile.Elevation * ElevationStep;
            return tile.Water ? y + ElevationStep * Rendering.SwimVisuals.SurfaceStepOffset : y;
        }

        private static bool PointInsideHex(float x, float z, HexLive.Simulation.Common.TileCoord coord)
        {
            var center = HexSpatialMath.TileToWorld(coord);
            var dx = Mathf.Abs(x - center.X);
            var dz = Mathf.Abs(z - center.Y);
            var radius = HexSpatialMath.HexRadius;
            return dz <= radius &&
                HexSpatialMath.Sqrt3 * dx + dz <= HexSpatialMath.Sqrt3 * radius;
        }

        // ---- Orbit mode ------------------------------------------------------

        private void EnterOrbit(int npcId)
        {
            _mode = Mode.Orbit;
            _orbitTargetId = npcId;
            _currentDistance = Mathf.Clamp(_orbitDistance, _orbitMinDistance, _orbitMaxDistance);
            _currentYaw = _orbitYaw;
            _currentPitch = _orbitPitch;
            _hasSmoothedTarget = false; // snap the pivot to the new NPC on entry
        }

        // Switching an already-followed character is not a second orbit entry:
        // framing survives and only smoothing velocities are reset. The next
        // snapshot target is accepted immediately by UpdateOrbit.
        private void SwitchOrbitTarget(int npcId)
        {
            if (_orbitTargetId == npcId)
            {
                return;
            }

            _orbitTargetId = npcId;
            _targetVelocity = Vector3.zero;
            _velocity = Vector3.zero;
            _yawVelocity = 0f;
            _pitchVelocity = 0f;
            _hasSmoothedTarget = false;
        }

        private void SwitchOrbitTarget(WorldSnapshot snapshot, int npcId)
        {
            SwitchOrbitTarget(npcId);
            if (!TryGetOrbitTarget(snapshot, npcId, out var target))
            {
                return;
            }

            _smoothedTarget = target;
            _hasSmoothedTarget = true;
            // Preserve yaw, pitch and distance, but make both pivot and rig use
            // the selected NPC in this same LateUpdate frame.
            _smoothedYaw = _currentYaw;
            _smoothedPitch = _currentPitch;
        }

        /// <summary>
        /// Place the rig on the selected NPC immediately. The opening world is
        /// intentionally paused, so SmoothDamp (scaled delta time) cannot move
        /// the camera before the loading curtain fades.
        /// </summary>
        public bool SnapToSelectedTarget()
        {
            if (!NpcSelection.HasSelection)
            {
                return false;
            }

            var snapshot = _runner != null && _runner.IsReady ? _runner.CreateSnapshot() : null;
            if (snapshot == null ||
                !TryGetOrbitTarget(snapshot, NpcSelection.SelectedId, out var target))
            {
                return false;
            }

            if (_mode != Mode.Orbit || _orbitTargetId != NpcSelection.SelectedId)
            {
                EnterOrbit(NpcSelection.SelectedId);
            }

            _camera ??= GetComponent<Camera>();
            _smoothedTarget = target;
            _targetVelocity = Vector3.zero;
            _hasSmoothedTarget = true;
            _smoothedYaw = _currentYaw;
            _smoothedPitch = _currentPitch;
            _yawVelocity = 0f;
            _pitchVelocity = 0f;
            _velocity = Vector3.zero;

            var rotation = Quaternion.Euler(_smoothedPitch, _smoothedYaw, 0f);
            var uiLift = 0f;
            var coverage = NpcSelection.BottomUiCoverage;
            if (_camera != null && coverage > 0.001f)
            {
                var frustumHeight = 2f * _currentDistance *
                    Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
                uiLift = frustumHeight * coverage * 0.5f;
            }

            transform.SetPositionAndRotation(
                target - rotation * Vector3.forward * _currentDistance -
                rotation * Vector3.up * uiLift,
                rotation);
            return true;
        }

        private void ExitOrbit()
        {
            _mode = Mode.Free;
            // Deselecting only unhooks the pivot from the character: it stays
            // exactly where they stood (magnetised to that hex's ground) and
            // the angle, distance and framing are left untouched, so the view
            // does not fly back up to a top-down shot.
            _orbitDistance = _currentDistance;
            var pivot = _hasSmoothedTarget ? _smoothedTarget : transform.position;
            _freePivot = SnapToGround(new Vector3(pivot.x, pivot.y, pivot.z));
            _velocity = Vector3.zero;
        }

        private void UpdateOrbit()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
            {
                NpcSelection.Clear();
                return;
            }

            var snapshot = _runner != null && _runner.IsReady ? _runner.CreateSnapshot() : null;
            if (snapshot == null)
            {
                return;
            }

            // Left/right arrows cycle to the previous/next character (arrows
            // aren't used for panning while orbiting).
            HandleOrbitCycle(snapshot);

            // Re-pick: clicking another NPC while orbiting switches focus to it
            // (left click is free here — rotation uses the right button).
            TryHandleLeftClick(snapshot);

            if (!TryGetOrbitTarget(snapshot, _orbitTargetId, out var rawTarget))
            {
                // The followed NPC is gone (died) — fall back to free camera.
                NpcSelection.Clear();
                return;
            }

            // The NPC itself is the exact target every frame. Only ApplyRig
            // smooths the camera's catch-up; smoothing this pivot as well would
            // introduce a second lag and leave the subject visibly off-centre.
            _smoothedTarget = rawTarget;
            _targetVelocity = Vector3.zero;
            _hasSmoothedTarget = true;

            HandleOrbitInput();

            _currentPitch = Mathf.Clamp(_currentPitch, _orbitMinPitch, _orbitMaxPitch);
            ApplyRig(_smoothedTarget, _orbitPositionSmooth);
        }

        // Cycle the followed character with the left/right arrow keys.
        private void HandleOrbitCycle(HexLive.Simulation.Debug.WorldSnapshot snapshot)
        {
            var keyboard = Keyboard.current;
            if (keyboard == null || snapshot.Npcs.Count == 0)
            {
                return;
            }

            var dir = 0;
            if (keyboard.leftArrowKey.wasPressedThisFrame || keyboard.aKey.wasPressedThisFrame)
            {
                dir = -1;
            }
            else if (keyboard.rightArrowKey.wasPressedThisFrame || keyboard.dKey.wasPressedThisFrame)
            {
                dir = 1;
            }

            if (dir == 0)
            {
                return;
            }

            _orbitRoster.Clear();
            for (var i = 0; i < snapshot.Npcs.Count; i++)
            {
                _orbitRoster.Add(snapshot.Npcs[i].Id.Value);
            }

            _orbitRoster.Sort();
            var count = _orbitRoster.Count;
            var index = 0;
            for (var i = 0; i < count; i++)
            {
                if (_orbitRoster[i] == _orbitTargetId)
                {
                    index = i;
                    break;
                }
            }

            var next = ((index + dir) % count + count) % count;
            var nextId = _orbitRoster[next];
            SwitchOrbitTarget(snapshot, nextId);
            NpcSelection.Select(nextId);
        }

        private void HandleOrbitInput()
        {
            var mouse = Mouse.current;
            if (mouse == null)
            {
                return;
            }

            var scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.001f)
            {
                // Proportional zoom: fine steps up close, big sweeps far out.
                var step = _orbitZoomSpeed * 0.01f * Mathf.Max(0.15f, _currentDistance / 9f);
                _currentDistance = Mathf.Clamp(
                    _currentDistance - scroll * step,
                    _orbitMinDistance, _orbitMaxDistance);
            }

            if (!mouse.rightButton.isPressed)
            {
                return;
            }

            var delta = mouse.delta.ReadValue();
            _currentYaw += delta.x * _orbitRotationSpeed;
            _currentPitch -= delta.y * _orbitRotationSpeed;
            _currentPitch = Mathf.Clamp(_currentPitch, _orbitMinPitch, _orbitMaxPitch);
        }

        // The orbit pivot: the pose-aware center of the body (bone-derived) —
        // chest height standing, and it follows the body down when the NPC
        // sits or lies, so the character stays centered in frame. Fallbacks:
        // feet + neck offset, then the raw snapshot position.
        private bool TryGetOrbitTarget(
            HexLive.Simulation.Debug.WorldSnapshot snapshot, int npcId, out Vector3 target)
        {
            var neck = SimulationUnityMapper.HexRadius * _orbitNeckFactor;

            if (_worldRenderer == null)
            {
                _worldRenderer = FindAnyObjectByType<HexWorldRenderer>();
            }

            if (_worldRenderer != null && _worldRenderer.TryGetNpcBodyCenter(npcId, out var bodyCenter))
            {
                target = bodyCenter;
                return true;
            }

            if (_worldRenderer != null && _worldRenderer.TryGetNpcViewPosition(npcId, out var viewPos))
            {
                target = viewPos + Vector3.up * neck;
                return true;
            }

            foreach (var npc in snapshot.Npcs)
            {
                if (npc.Id.Value == npcId)
                {
                    target = SimulationUnityMapper.ToUnityPosition(
                        npc.Position, SimulationUnityMapper.TileHeight + neck);
                    return true;
                }
            }

            target = Vector3.zero;
            return false;
        }

    }
}
