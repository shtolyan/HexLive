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
    ///     WASD slides it, while arrows/right-drag/two-finger horizontal
    ///     swipe rotate and scroll zooms.
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

        [Header("Keyboard rotation")]
        [SerializeField] private float _keyboardYawSpeed = 90f;
        [SerializeField] private float _keyboardPitchSpeed = 70f;

        [Header("Trackpad")]
        [Tooltip("Yaw degrees per horizontal scroll unit (two-finger swipe rotates like the arrow keys). Negative flips direction.")]
        [SerializeField] private float _scrollYawSpeed = 1.5f;

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

        // Shared rig state (both modes drive the same yaw/pitch/distance).
        // Smoothing happens in PARAMETER space — pivot, yaw, pitch and distance
        // each damp independently and the transform is computed exactly from
        // the rig equation. Smoothing the world position separately (the old
        // scheme) made the subject slide off-centre during rotation: the
        // desired position sweeps an arc while positional damping cuts the
        // chord.
        private float _currentYaw;
        private float _currentPitch;
        private float _currentDistance;
        private float _smoothedYaw;
        private float _smoothedPitch;
        private float _smoothedDistance;
        private float _yawVelocity;
        private float _pitchVelocity;
        private float _distanceVelocity;
        private float _requestedDistance;
        private Vector3 _smoothedPivot;
        private Vector3 _pivotVelocity;
        private bool _hasSmoothedPivot;

        // Orbit-mode state.
        private Vector3 _smoothedTarget;
        private bool _hasSmoothedTarget;
        private bool _pendingFrameSelection;

        // §123 selection marquee. A click is dispatched on release; once the
        // pointer travels beyond this threshold the same gesture becomes a
        // colony-only box selection instead of a manual move order.
        private const float SelectionDragThresholdPixels = 6f;
        private bool _leftPressActive;
        private bool _selectionDragging;
        private Vector2 _leftPressPosition;
        private Vector2 _selectionDragPosition;

        private Camera _camera;
        private HexWorldRenderer _worldRenderer;

        // Ground magnet: tile top heights, refreshed periodically so the free
        // pivot can hug elevation without rebuilding a snapshot every frame.
        private readonly Dictionary<TileCoord, float> _tileTops = new();
        private float _tileTopsStamp = float.NegativeInfinity;
        private const float TileTopsRefreshSeconds = 0.5f;

        private const float ElevationStep = 0.55f;

        // §121: ручной ввод живёт рядом на той же камере и получает клик
        // первым. Ссылка ищется лениво — компонент навешивает бутстрап.
        private SimulationInputAdapter _manualInput;

        public void SetRunner(SimulationRunnerBehaviour runner)
        {
            _runner = runner;
        }

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
            _requestedDistance = _currentDistance;
            _smoothedDistance = _currentDistance;

            // The start pose looks straight down, so the pivot is simply the
            // ground under the camera.
            _freePivot = SnapToGround(new Vector3(_startPosition.x, 0f, _startPosition.z));
        }

        private void OnEnable()
        {
            NpcSelection.SelectionChanged += OnSelectionChanged;
            NpcSelection.CameraRequested += OnCameraRequested;
        }

        private void OnDisable()
        {
            NpcSelection.SelectionChanged -= OnSelectionChanged;
            NpcSelection.CameraRequested -= OnCameraRequested;
        }

        // §123: selection no longer implies follow. Losing every selected actor
        // detaches; changing a non-empty set leaves the current camera mode in
        // place and the explicit CameraRequested event decides frame/follow.
        private void OnSelectionChanged(System.Collections.Generic.IReadOnlyList<int> selection)
        {
            if (!NpcSelection.HasSelection && _mode == Mode.Orbit)
            {
                ExitOrbit();
            }
            else if (_mode == Mode.Orbit)
            {
                _hasSmoothedTarget = false;
            }
        }

        private void OnCameraRequested(NpcSelection.CameraRequest request)
        {
            if (!NpcSelection.HasSelection) return;
            if (request == NpcSelection.CameraRequest.Frame)
            {
                _pendingFrameSelection = true;
                return;
            }

            if (_mode == Mode.Orbit)
            {
                ExitOrbit();
            }
            else
            {
                EnterOrbitSelection();
            }
        }

        private void LateUpdate()
        {
            if (_runner == null)
            {
                _runner = FindAnyObjectByType<SimulationRunnerBehaviour>();
            }

            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
            {
                if (UI.ContextMenuPanel.IsOpen)
                {
                    UI.ContextMenuPanel.Close();
                }
                else
                {
                    NpcSelection.Clear();
                }
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
            var snapshot = _runner != null && _runner.IsReady ? _runner.CreateSnapshot() : null;
            if (_pendingFrameSelection && snapshot != null)
            {
                _pendingFrameSelection = false;
                FrameSelection(snapshot);
            }

            if (!UI.GameMenu.IsOpen && !UI.EndSummaryPanel.IsOpen)
            {
                HandlePan();
                HandleZoom();
                HandleScrollYaw();
                HandleFreeRotation();
                HandlePointerGesture(snapshot);
            }

            _freePivot = SnapToGround(_freePivot);

            ApplyRig(_freePivot, _panSmooth, _panSmooth);
        }

        private void HandlePan()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            var input = Vector2.zero;

            if (keyboard.wKey.isPressed) input.y += 1f;
            if (keyboard.sKey.isPressed) input.y -= 1f;
            if (keyboard.dKey.isPressed) input.x += 1f;
            if (keyboard.aKey.isPressed) input.x -= 1f;
            if (keyboard.upArrowKey.isPressed) input.y += 1f;
            if (keyboard.downArrowKey.isPressed) input.y -= 1f;
            if (keyboard.rightArrowKey.isPressed) input.x += 1f;
            if (keyboard.leftArrowKey.isPressed) input.x -= 1f;

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
            _requestedDistance = _currentDistance;
        }

        private void HandleKeyboardRotation(float maxPitch)
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            var yaw = 0f;
            var pitch = 0f;
            if (keyboard.leftArrowKey.isPressed) yaw -= 1f;
            if (keyboard.rightArrowKey.isPressed) yaw += 1f;
            if (keyboard.upArrowKey.isPressed) pitch -= 1f;
            if (keyboard.downArrowKey.isPressed) pitch += 1f;
            if (Mathf.Abs(yaw) < 0.01f && Mathf.Abs(pitch) < 0.01f) return;

            _currentYaw += yaw * _keyboardYawSpeed * Time.unscaledDeltaTime;
            _currentPitch = Mathf.Clamp(
                _currentPitch + pitch * _keyboardPitchSpeed * Time.unscaledDeltaTime,
                _orbitMinPitch, maxPitch);
        }

        // Трекпад: двухпальцевый горизонтальный свайп (scroll.x) крутит риг,
        // как стрелки, без нажатия кнопок. Вертикальная ось остаётся зумом.
        // Диагональный жест даёт зум и поворот одновременно — это штатное
        // поведение тачпада, дедзона сверх порога дребезга не нужна.
        private void HandleScrollYaw()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;

            var scrollX = mouse.scroll.ReadValue().x;
            if (Mathf.Abs(scrollX) < 0.01f) return;

            _currentYaw += scrollX * _scrollYawSpeed;
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
        // Every parameter damps on its own; the transform then sits EXACTLY on
        // the rig equation, so rotation can never desynchronise from position.
        private void ApplyRig(Vector3 pivot, float pivotSmooth, float distanceSmooth)
        {
            if (!_hasSmoothedPivot)
            {
                _smoothedPivot = pivot;
                _pivotVelocity = Vector3.zero;
                _hasSmoothedPivot = true;
            }
            else
            {
                _smoothedPivot = Vector3.SmoothDamp(
                    _smoothedPivot, pivot, ref _pivotVelocity, pivotSmooth,
                    Mathf.Infinity, Time.unscaledDeltaTime);
            }

            _smoothedYaw = Mathf.SmoothDampAngle(
                _smoothedYaw, _currentYaw, ref _yawVelocity, _orbitRotationSmooth,
                Mathf.Infinity, Time.unscaledDeltaTime);
            _smoothedPitch = Mathf.SmoothDampAngle(
                _smoothedPitch, _currentPitch, ref _pitchVelocity, _orbitRotationSmooth,
                Mathf.Infinity, Time.unscaledDeltaTime);
            _smoothedDistance = Mathf.SmoothDamp(
                _smoothedDistance, _currentDistance, ref _distanceVelocity,
                distanceSmooth, Mathf.Infinity, Time.unscaledDeltaTime);

            var rotation = Quaternion.Euler(_smoothedPitch, _smoothedYaw, 0f);

            // The character bar covers the bottom of the screen, so aiming the
            // pivot at the screen center hides the NPC's legs behind the UI.
            // Slide the frame down by half the bar's coverage: the pivot then
            // lands in the middle of the strip that stays visible above the bar.
            // With nothing selected the bar is hidden and the lift is zero.
            // Offsets derive from the SMOOTHED distance so the framing animates
            // in lockstep with the zoom instead of leading it.
            var uiLift = 0f;
            var uiSide = 0f;
            var coverage = NpcSelection.BottomUiCoverage;
            if (_camera != null && coverage > 0.001f)
            {
                var frustumHeight = 2f * _smoothedDistance *
                    Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
                uiLift = frustumHeight * coverage * 0.5f;
            }

            var rightCoverage = NpcSelection.RightUiCoverage;
            if (_camera != null && rightCoverage > 0.001f)
            {
                var frustumHeight = 2f * _smoothedDistance *
                    Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
                uiSide = frustumHeight * _camera.aspect * rightCoverage * 0.5f;
            }

            var desiredPosition = _smoothedPivot
                - rotation * Vector3.forward * _smoothedDistance
                - rotation * Vector3.up * uiLift
                + rotation * Vector3.right * uiSide;

            transform.SetPositionAndRotation(desiredPosition, rotation);
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

        private void HandlePointerGesture(WorldSnapshot snapshot)
        {
            var mouse = Mouse.current;
            if (mouse == null) return;

            var pointer = mouse.position.ReadValue();
            if (mouse.leftButton.wasPressedThisFrame)
            {
                _leftPressActive = !PointerBlockedForWorld();
                _selectionDragging = false;
                _leftPressPosition = pointer;
                _selectionDragPosition = pointer;
            }

            if (_leftPressActive && mouse.leftButton.isPressed)
            {
                _selectionDragPosition = pointer;
                if (!_selectionDragging &&
                    Vector2.Distance(_leftPressPosition, pointer) >= SelectionDragThresholdPixels)
                {
                    _selectionDragging = true;
                }
            }

            if (!_leftPressActive || !mouse.leftButton.wasReleasedThisFrame)
            {
                return;
            }

            _leftPressActive = false;
            _selectionDragPosition = pointer;
            if (_selectionDragging)
            {
                _selectionDragging = false;
                ApplyMarqueeSelection(snapshot, _leftPressPosition, pointer);
                return;
            }

            TryHandleLeftClick(pointer, snapshot);
        }

        private static bool PointerBlockedForWorld() =>
            NpcSelection.PointerOverUi || UI.HexInspectorPanel.PointerOverPanel ||
            UI.ContextMenuPanel.PointerOverPanel || UI.GameMenu.IsOpen ||
            UI.EndSummaryPanel.IsOpen;

        private void ApplyMarqueeSelection(WorldSnapshot snapshot, Vector2 from, Vector2 to)
        {
            if (snapshot == null || _camera == null) return;
            var minX = Mathf.Min(from.x, to.x);
            var maxX = Mathf.Max(from.x, to.x);
            var minY = Mathf.Min(from.y, to.y);
            var maxY = Mathf.Max(from.y, to.y);
            var ids = new List<int>();
            foreach (var npc in snapshot.Npcs)
            {
                if (npc.Faction != HexLive.Simulation.Agents.Faction.Colony || npc.Health <= 0f)
                {
                    continue;
                }

                var world = SimulationUnityMapper.ToUnityPosition(
                    npc.Position, SimulationUnityMapper.CameraTargetHeight);
                var screen = _camera.WorldToScreenPoint(world);
                if (screen.z > 0f && screen.x >= minX && screen.x <= maxX &&
                    screen.y >= minY && screen.y <= maxY)
                {
                    ids.Add(npc.Id.Value);
                }
            }

            ids.Sort();
            var add = Keyboard.current != null &&
                (Keyboard.current.leftShiftKey.isPressed || Keyboard.current.rightShiftKey.isPressed);
            if (add) NpcSelection.AddMany(ids);
            else NpcSelection.ReplaceMany(ids);
        }

        private void OnGUI()
        {
            if (!_selectionDragging) return;
            var x = Mathf.Min(_leftPressPosition.x, _selectionDragPosition.x);
            var width = Mathf.Abs(_selectionDragPosition.x - _leftPressPosition.x);
            var bottom = Mathf.Min(_leftPressPosition.y, _selectionDragPosition.y);
            var height = Mathf.Abs(_selectionDragPosition.y - _leftPressPosition.y);
            var rect = new Rect(x, Screen.height - bottom - height, width, height);

            var previous = GUI.color;
            GUI.color = new Color(0.25f, 0.68f, 1f, 0.16f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = new Color(0.35f, 0.75f, 1f, 0.9f);
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, 1f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.y, 1f, rect.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), Texture2D.whiteTexture);
            GUI.color = previous;
        }

        // Left-click first tries an NPC, then falls back to the map hex under
        // the cursor. Dispatch happens on release so the same press can become
        // a selection marquee without also issuing a move order.
        private void TryHandleLeftClick(Vector2 mousePosition, WorldSnapshot snapshot = null)
        {
            if (PointerBlockedForWorld()) return;

            var shift = Keyboard.current != null &&
                (Keyboard.current.leftShiftKey.isPressed || Keyboard.current.rightShiftKey.isPressed);

            // Shift-picking own actors is a selection gesture even when manual
            // input would otherwise open an action menu.
            if (shift && TryPickNpc(mousePosition, snapshot, additiveOnly: true))
            {
                HexSelection.Clear();
                return;
            }

            // §121: пока выбранной колонисткой управляет игрок, тот же клик
            // значит другое — идти, открыть меню действий, закрыть открытое.
            // Съеденный клик до обычной обработки не доходит.
            if (_manualInput == null)
            {
                _manualInput = GetComponent<SimulationInputAdapter>();
            }

            if (_manualInput != null && _manualInput.TryHandleManualClick(mousePosition))
            {
                return;
            }

            if (TryPickNpc(mousePosition, snapshot))
            {
                HexSelection.Clear();
                return;
            }

            if (HexSelection.Enabled && TryPickHex(mousePosition, out var coord, snapshot))
            {
                HexSelection.Select(coord);
            }
        }

        // Left-click on an NPC -> enter orbit mode.
        private bool TryPickNpc(
            Vector2 mousePos, WorldSnapshot currentSnapshot = null, bool additiveOnly = false)
        {
            if (_camera == null)
            {
                _camera = GetComponent<Camera>();
                if (_camera == null) return false;
            }

            var snapshot = currentSnapshot ??
                (_runner != null && _runner.IsReady ? _runner.CreateSnapshot() : null);
            if (snapshot == null ||
                (snapshot.Npcs.Count == 0 && snapshot.Corpses.Count == 0))
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

                foreach (var npc in PickablePeople(snapshot))
                {
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
                    return ApplyNpcPick(snapshot, viewHitId, additiveOnly);
                }
            }

            // A just-spawned actor may not have completed its visual setup yet,
            // and prototype scenes still use primitive NPC views. Preserve the
            // point-radius fallback for those cases only.
            var bestId = -1;
            var bestDist = _pickRadiusPixels;

            foreach (var npc in PickablePeople(snapshot))
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
                return ApplyNpcPick(snapshot, bestId, additiveOnly);
            }

            return false;
        }

        private static bool ApplyNpcPick(WorldSnapshot snapshot, int npcId, bool additiveOnly)
        {
            foreach (var npc in snapshot.Npcs)
            {
                if (npc.Id.Value != npcId) continue;
                var shift = Keyboard.current != null &&
                    (Keyboard.current.leftShiftKey.isPressed || Keyboard.current.rightShiftKey.isPressed);
                if ((additiveOnly || shift) &&
                    npc.Faction == HexLive.Simulation.Agents.Faction.Colony && npc.Health > 0f)
                {
                    NpcSelection.Toggle(npcId);
                    return true;
                }

                if (additiveOnly) return false;
                // Outsiders are always exclusive by construction.
                NpcSelection.Activate(npcId);
                return true;
            }

            foreach (var corpse in snapshot.Corpses)
            {
                if (corpse.Id.Value != npcId) continue;
                if (additiveOnly) return false;
                NpcSelection.Activate(npcId);
                return true;
            }

            return false;
        }

        private static IEnumerable<NpcSnapshot> PickablePeople(WorldSnapshot snapshot)
        {
            foreach (var npc in snapshot.Npcs) yield return npc;
            foreach (var corpse in snapshot.Corpses) yield return corpse;
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

        private void EnterOrbitSelection()
        {
            _mode = Mode.Orbit;
            _requestedDistance = Mathf.Clamp(_currentDistance, _orbitMinDistance, _orbitMaxDistance);
            _hasSmoothedTarget = false;
            // _smoothedPivot deliberately survives the transition: pivot
            // smoothing is what glides the camera from the ground point to the
            // NPC on orbit entry.
        }

        private void FrameSelection(WorldSnapshot snapshot)
        {
            if (!TryGetSelectionFrame(snapshot, out var center, out var radius))
            {
                return;
            }

            _freePivot = SnapToGround(center);
            var fit = FitDistance(radius);
            _requestedDistance = fit;
            _currentDistance = fit;
            _pivotVelocity = Vector3.zero;
        }

        private bool TryGetSelectionFrame(
            WorldSnapshot snapshot, out Vector3 center, out float radius)
        {
            center = Vector3.zero;
            radius = 0f;
            var min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            var found = 0;
            var ids = NpcSelection.SelectedIds;
            for (var i = 0; i < ids.Count; i++)
            {
                if (!TryGetOrbitTarget(snapshot, ids[i], out var target)) continue;
                min = Vector3.Min(min, target);
                max = Vector3.Max(max, target);
                found++;
            }

            if (found == 0) return false;
            center = (min + max) * 0.5f;
            var extent = max - min;
            radius = Mathf.Max(0.65f, 0.5f * Mathf.Sqrt(
                extent.x * extent.x + extent.y * extent.y + extent.z * extent.z) + 0.75f);
            return true;
        }

        private float FitDistance(float radius)
        {
            _camera ??= GetComponent<Camera>();
            if (_camera == null) return Mathf.Clamp(_orbitDistance, _orbitMinDistance, _orbitMaxDistance);

            var visibleHeight = Mathf.Clamp(1f - NpcSelection.BottomUiCoverage, 0.35f, 1f);
            var visibleWidth = Mathf.Clamp(1f - NpcSelection.RightUiCoverage, 0.35f, 1f);
            var verticalHalf = Mathf.Atan(
                Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * visibleHeight);
            var horizontalHalf = Mathf.Atan(
                Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad) *
                _camera.aspect * visibleWidth);
            var limitingHalf = Mathf.Max(5f * Mathf.Deg2Rad,
                Mathf.Min(verticalHalf, horizontalHalf));
            var fit = radius / Mathf.Tan(limitingHalf) + 1.25f;
            return Mathf.Clamp(Mathf.Max(_orbitDistance, fit), _orbitMinDistance, _orbitMaxDistance);
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
            if (snapshot == null || !TryGetSelectionFrame(snapshot, out var target, out var radius))
            {
                return false;
            }

            _mode = Mode.Free;
            _camera ??= GetComponent<Camera>();
            _freePivot = SnapToGround(target);
            _requestedDistance = FitDistance(radius);
            _currentDistance = _requestedDistance;
            _smoothedYaw = _currentYaw;
            _smoothedPitch = _currentPitch;
            _yawVelocity = 0f;
            _pitchVelocity = 0f;
            // Snap every smoothed parameter: the next LateUpdate must
            // reproduce this exact pose with zero first-frame glide.
            _smoothedPivot = _freePivot;
            _hasSmoothedPivot = true;
            _pivotVelocity = Vector3.zero;
            _smoothedDistance = _currentDistance;
            _distanceVelocity = 0f;

            var rotation = Quaternion.Euler(_smoothedPitch, _smoothedYaw, 0f);
            var uiLift = 0f;
            var uiSide = 0f;
            var coverage = NpcSelection.BottomUiCoverage;
            if (_camera != null && coverage > 0.001f)
            {
                var frustumHeight = 2f * _currentDistance *
                    Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
                uiLift = frustumHeight * coverage * 0.5f;
            }

            if (_camera != null && NpcSelection.RightUiCoverage > 0.001f)
            {
                var frustumHeight = 2f * _currentDistance *
                    Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
                uiSide = frustumHeight * _camera.aspect * NpcSelection.RightUiCoverage * 0.5f;
            }

            transform.SetPositionAndRotation(
                _freePivot - rotation * Vector3.forward * _currentDistance -
                rotation * Vector3.up * uiLift + rotation * Vector3.right * uiSide,
                rotation);
            return true;
        }

        private void ExitOrbit()
        {
            _mode = Mode.Free;
            // Deselecting only unhooks the pivot from the character: it stays
            // exactly where they stood (magnetised to that hex's ground) and
            // the angle, distance and framing are left untouched, so the view
            // does not fly back up to a top-down shot. _orbitDistance is NOT
            // updated here: FitDistance floors explicit Frame requests with it,
            // and mutating it would turn the last free-camera height into a
            // creeping zoom minimum.
            _requestedDistance = _currentDistance;
            var pivot = _hasSmoothedTarget ? _smoothedTarget : transform.position;
            _freePivot = SnapToGround(new Vector3(pivot.x, pivot.y, pivot.z));
            _pivotVelocity = Vector3.zero;
        }

        private void UpdateOrbit()
        {
            var snapshot = _runner != null && _runner.IsReady ? _runner.CreateSnapshot() : null;
            if (snapshot == null)
            {
                return;
            }

            if (HasPanInput())
            {
                ExitOrbit();
                UpdateFree();
                return;
            }

            HandlePointerGesture(snapshot);

            if (!TryGetSelectionFrame(snapshot, out var rawTarget, out var radius))
            {
                NpcSelection.Clear();
                return;
            }

            // The NPC itself is the exact target every frame; ApplyRig's pivot
            // smoothing owns the catch-up.
            _smoothedTarget = rawTarget;
            _hasSmoothedTarget = true;

            // Auto-framing (fit) applies ONLY on an explicit Frame request —
            // it must never act as a per-frame floor, or scroll could no longer
            // reach the close-up minimum.
            if (_pendingFrameSelection)
            {
                _pendingFrameSelection = false;
                _requestedDistance = FitDistance(radius);
            }

            HandleOrbitInput();
            HandleKeyboardRotation(_orbitMaxPitch);
            HandleScrollYaw();

            _currentDistance = Mathf.Clamp(
                _requestedDistance, _orbitMinDistance, _orbitMaxDistance);

            _currentPitch = Mathf.Clamp(_currentPitch, _orbitMinPitch, _orbitMaxPitch);
            ApplyRig(_smoothedTarget, _orbitTargetSmooth, _orbitPositionSmooth);
        }

        private static bool HasPanInput()
        {
            var keyboard = Keyboard.current;
            return keyboard != null && (keyboard.wKey.isPressed || keyboard.aKey.isPressed ||
                keyboard.sKey.isPressed || keyboard.dKey.isPressed);
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
                _requestedDistance = Mathf.Clamp(
                    _requestedDistance - scroll * step,
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


            foreach (var corpse in snapshot.Corpses)
            {
                if (corpse.Id.Value == npcId)
                {
                    target = SimulationUnityMapper.ToUnityPosition(
                        corpse.Position, SimulationUnityMapper.TileHeight + neck);
                    return true;
                }
            }

            target = Vector3.zero;
            return false;
        }

    }
}
