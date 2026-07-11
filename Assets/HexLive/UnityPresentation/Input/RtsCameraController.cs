using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.Spatial;
using UnityEngine;
using UnityEngine.InputSystem;

namespace HexLive.UnityPresentation.Input
{
    /// <summary>
    /// RTS-style camera with two modes:
    ///   • Free: fixed top-down angle, WASD/arrows to pan, scroll to zoom.
    ///   • Orbit: click an NPC to lock the camera in an orbit behind them;
    ///     right-drag to rotate, scroll to zoom in/out, Escape to return.
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
        [SerializeField] private float _orbitPositionSmooth = 0.14f;
        [SerializeField] private float _orbitRotationSmooth = 0.06f;
        [SerializeField] private float _pickRadiusPixels = 70f;

        [Tooltip("Orbit pivot height above the NPC's feet, as a fraction of the hex radius (neck ≈ 0.62).")]
        [SerializeField] private float _orbitNeckFactor = 0.62f;

        [Tooltip("How smoothly the pivot follows the NPC. Bigger = softer/laggier follow.")]
        [SerializeField] private float _orbitTargetSmooth = 0.1f;

        private enum Mode
        {
            Free,
            Orbit
        }

        private Mode _mode = Mode.Free;

        // Free-mode state.
        private Vector3 _targetPosition;
        private float _targetHeight;
        private Vector3 _velocity;

        // Orbit-mode state.
        private int _orbitTargetId;
        private float _currentYaw;
        private float _currentPitch;
        private float _yawVelocity;
        private float _pitchVelocity;
        private Vector3 _orbitVelocity;
        private Vector3 _smoothedTarget;
        private Vector3 _targetVelocity;
        private bool _hasSmoothedTarget;

        private Camera _camera;
        private HexWorldRenderer _worldRenderer;

        public void SetRunner(SimulationRunnerBehaviour runner)
        {
            _runner = runner;
        }

        private void Start()
        {
            _camera = GetComponent<Camera>();
            transform.position = _startPosition;
            transform.rotation = Quaternion.Euler(_startRotation);
            _targetPosition = _startPosition;
            _targetHeight = _startPosition.y;
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
                EnterOrbit(npcId);
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
            HandlePan();
            HandleZoom();
            ApplyFreeMovement();
            TryPickNpc();
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

            var heightFactor = Mathf.Lerp(0.5f, 1.5f, Mathf.InverseLerp(_minHeight, _maxHeight, _targetHeight));
            var speed = _panSpeed * heightFactor * Time.unscaledDeltaTime;

            // Pan along the camera's own axes projected onto the ground so
            // screen-right/up match the arrow pressed. The camera looks
            // straight down with a 180° yaw, so raw world X/Z would invert.
            var rotation = Quaternion.Euler(_startRotation);
            var right = rotation * Vector3.right;
            var up = rotation * Vector3.up; // screen-up on the ground for a top-down cam
            right.y = 0f;
            up.y = 0f;
            right.Normalize();
            up.Normalize();

            var move = (right * input.x + up * input.y) * speed;
            _targetPosition.x += move.x;
            _targetPosition.z += move.z;
        }

        private void HandleZoom()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;

            var scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) < 0.01f) return;

            _targetHeight -= scroll * _zoomSpeed * 0.01f;
            _targetHeight = Mathf.Clamp(_targetHeight, _minHeight, _maxHeight);
        }

        private void ApplyFreeMovement()
        {
            var desired = new Vector3(_targetPosition.x, _targetHeight, _targetPosition.z);
            transform.position = Vector3.SmoothDamp(transform.position, desired, ref _velocity, _panSmooth);
            transform.rotation = Quaternion.Euler(_startRotation);
        }

        // Left-click on an NPC → enter orbit mode.
        private void TryPickNpc()
        {
            var mouse = Mouse.current;
            if (mouse == null || !mouse.leftButton.wasPressedThisFrame)
            {
                return;
            }

            // Don't pick NPCs hidden behind the character bar.
            if (NpcSelection.PointerOverUi)
            {
                return;
            }

            if (_camera == null)
            {
                _camera = GetComponent<Camera>();
                if (_camera == null) return;
            }

            var snapshot = _runner != null && _runner.IsReady ? _runner.CreateSnapshot() : null;
            if (snapshot == null || snapshot.Npcs.Count == 0)
            {
                return;
            }

            var mousePos = mouse.position.ReadValue();
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
            }
        }

        // ---- Orbit mode ------------------------------------------------------

        private void EnterOrbit(int npcId)
        {
            _mode = Mode.Orbit;
            _orbitTargetId = npcId;
            _orbitDistance = Mathf.Clamp(_orbitDistance, _orbitMinDistance, _orbitMaxDistance);
            _currentYaw = _orbitYaw;
            _currentPitch = _orbitPitch;
            _orbitVelocity = Vector3.zero;
            _hasSmoothedTarget = false; // snap the pivot to the new NPC on entry
        }

        private void ExitOrbit()
        {
            _mode = Mode.Free;
            // Return to a top-down view centered on the character we were
            // watching, so Escape leaves them in frame instead of snapping to
            // a random spot.
            var pivot = _hasSmoothedTarget ? _smoothedTarget : transform.position;
            _targetPosition = new Vector3(pivot.x, _targetHeight, pivot.z);
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
            TryPickNpc();

            if (!TryGetOrbitTarget(snapshot, _orbitTargetId, out var rawTarget))
            {
                // The followed NPC is gone (died) — fall back to free camera.
                NpcSelection.Clear();
                return;
            }

            // Follow the pivot smoothly so the NPC's slightly stepped motion
            // doesn't jerk the camera.
            if (!_hasSmoothedTarget)
            {
                _smoothedTarget = rawTarget;
                _targetVelocity = Vector3.zero;
                _hasSmoothedTarget = true;
            }
            else
            {
                _smoothedTarget = Vector3.SmoothDamp(
                    _smoothedTarget, rawTarget, ref _targetVelocity, _orbitTargetSmooth);
            }

            var target = _smoothedTarget;

            HandleOrbitInput();

            _currentPitch = Mathf.Clamp(_currentPitch, _orbitMinPitch, _orbitMaxPitch);
            var smoothedYaw = Mathf.SmoothDampAngle(
                NormalizeAngle(transform.eulerAngles.y), _currentYaw, ref _yawVelocity, _orbitRotationSmooth);
            var smoothedPitch = Mathf.SmoothDampAngle(
                transform.eulerAngles.x, _currentPitch, ref _pitchVelocity, _orbitRotationSmooth);

            var rotation = Quaternion.Euler(smoothedPitch, smoothedYaw, 0f);
            var desiredPosition = target - rotation * Vector3.forward * _orbitDistance;

            transform.position = Vector3.SmoothDamp(
                transform.position, desiredPosition, ref _orbitVelocity, _orbitPositionSmooth);
            transform.rotation = rotation;
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

            var count = snapshot.Npcs.Count;
            var index = 0;
            for (var i = 0; i < count; i++)
            {
                if (snapshot.Npcs[i].Id.Value == _orbitTargetId)
                {
                    index = i;
                    break;
                }
            }

            var next = ((index + dir) % count + count) % count;
            NpcSelection.Select(snapshot.Npcs[next].Id.Value);
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
                var step = _orbitZoomSpeed * 0.01f * Mathf.Max(0.15f, _orbitDistance / 9f);
                _orbitDistance = Mathf.Clamp(
                    _orbitDistance - scroll * step,
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

        private static float NormalizeAngle(float degrees)
        {
            degrees %= 360f;
            if (degrees > 180f) degrees -= 360f;
            return degrees;
        }
    }
}
