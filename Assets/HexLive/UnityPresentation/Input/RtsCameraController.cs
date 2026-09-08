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
    ///     WASD and the arrow keys slide it, while right-drag/two-finger
    ///     horizontal swipe rotate and scroll zooms.
    ///   • Orbit: a repeat click on the already selected NPC focuses on them
    ///     and follows; smoothing delays the catch-up but never changes its target.
    ///     One Escape (or any pan key) releases it where it settled — the
    ///     angle, distance and framing stay put instead of snapping back to a
    ///     top-down view. A second Escape clears the selection.
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

        [Tooltip("Доля дистанции за ОДИН щелчок колеса: 0.12 = на 12% ближе/дальше. " +
                 "После 32 wu остаётся настоящий мир, но мелкие детали заменяются маркерами; потолок 260 wu.")]
        [SerializeField] private float _zoomPerScrollTick = 0.12f;

        [Tooltip("Потолок накопленного за кадр скролла в щелчках. Трекпад шлёт поток " +
                 "событий вместо дискретных щелчков, и без потолка резкий свайп " +
                 "перепрыгивает весь диапазон за пару кадров.")]
        [SerializeField] private float _maxScrollTicksPerFrame = 1.5f;

        [Tooltip("Free-mode pitch ceiling. 90 = straight down (the classic RTS top view).")]
        [SerializeField] private float _freeMaxPitch = 90f;

        [Header("Orbit")]
        [SerializeField] private float _orbitDistance = 9f;
        [SerializeField] private float _orbitMinDistance = 0.7f; // close-up: face fills the frame
        [SerializeField] private float _orbitMaxDistance = 260f;
        [SerializeField] private float _orbitRotationSpeed = 0.2f;
        [SerializeField] private float _orbitPitch = 25f;
        [SerializeField] private float _orbitMinPitch = 5f;
        [SerializeField] private float _orbitMaxPitch = 80f;
        [SerializeField] private float _orbitYaw = 0f;
        [SerializeField] private float _orbitPositionSmooth = 0.18f;
        [SerializeField] private float _orbitRotationSmooth = 0.08f;
        [SerializeField] private float _pickRadiusPixels = 70f;

        [Header("Trackpad")]
        [Tooltip("Yaw degrees per horizontal scroll unit (two-finger swipe rotates like the arrow keys). Negative flips direction.")]
        [SerializeField] private float _scrollYawSpeed = 1.5f;

        [Header("Hex picking")]
        [SerializeField] private float _hexPickVerticalPadding = 0.04f;

        [Tooltip("Orbit pivot height above the NPC's feet, as a fraction of the hex radius (neck ≈ 0.62).")]
        [SerializeField] private float _orbitNeckFactor = 0.62f;

        [Tooltip("How smoothly the pivot catches up with the NPC. The NPC itself is always the target.")]
        [SerializeField] private float _orbitTargetSmooth = 0.18f;

        [Header("Distant overview")]
        [Tooltip("§150: мелкие детали скрываются после этого порога.")]
        [SerializeField] private float _overviewHideDistance = 32f;
        [Tooltip("§150: мелкие детали возвращаются только после этого порога.")]
        [SerializeField] private float _overviewShowDistance = 28f;
        [Tooltip("§150.4: дальше этого порога флора и вещи заменяются импосторами.")]
        [SerializeField] private float _overviewImpostorFarDistance = 64f;
        [Tooltip("§150.4: меши возвращаются только после этого порога.")]
        [SerializeField] private float _overviewImpostorNearDistance = 56f;

        private enum Mode
        {
            Free,
            Orbit
        }

        private Mode _mode = Mode.Free;

        // Free-mode state: a loose pivot glued to the hex ground.
        private Vector3 _freePivot;
        // §131: свободный пивот живёт не НА земле, а НАД ней — по умолчанию на
        // высоте головы стоящей (шея, _orbitNeckFactor), а после отцепления
        // слежения — на той высоте, где пивот был в момент отцепа. Это и
        // убирает прыжок «голова↔ноги» при вкл/выкл слежения.
        private float _freePivotHeight;

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
        private bool _overviewActive;
        private bool _overviewImpostorsActive;
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

        // Правая кнопка двулика: drag вращает риг, а клик без движения
        // открывает контекстное меню цели под курсором (атаковать/обобрать…),
        // не трогая ни выделение, ни слежение. Порог тот же, что у marquee.
        private bool _rightPressActive;
        private bool _rightDragging;
        private Vector2 _rightPressPosition;

        // Bug #279: был ли мир закрыт для указателя на ПРОШЛОМ кадре. Press,
        // начавшийся в кадр закрытия окна (окна закрываются на DOWN в
        // Update-фазе, раньше этого LateUpdate), миру не принадлежит.
        private bool _worldPointerBlockedLastFrame;
        private readonly HashSet<UnityEngine.Object> _selectionInputSuppressors = new();
        private readonly List<int> _visibleSelectionScratch = new();

        private Camera _camera;
        private HexWorldRenderer _worldRenderer;

        // Ground magnet: tile top heights, refreshed periodically so the free
        // pivot can hug elevation without rebuilding a snapshot every frame.
        private readonly Dictionary<TileCoord, float> _tileTops = new();
        private float _tileTopsStamp = float.NegativeInfinity;
        private const float TileTopsRefreshSeconds = 0.5f;

        private const float ElevationStep = 0.55f;
        // §112: full-3D distance from the lens to the nearest point of the
        // LeafGreen submesh bounds. The wider exit threshold prevents material
        // flicker, and the movement threshold avoids redundant palm scans.
        private const float PalmCrownHideDistance = 2.1f;
        private const float PalmCrownShowDistance = 2.5f;
        private const float PalmCrownCheckMovement = 0.1f;

        // §130 r5 = возврат к r1 (решение игрока: «первый раз самое лучшее
        // было»): камера почти вплотную (мин. зум 0.7) — NPC на несколько
        // секунд смотрит в объектив. Вход/выход с гистерезисом, метры от
        // объектива до лица; пере-взгляд той же NPC не раньше кулдауна.
        // Конусы/блендеры r2-r4 сняты — от них взгляд дёргался и не читался.
        private const float CloseUpGazeEnterDistance = 2.0f;
        private const float CloseUpGazeExitDistance = 2.6f;
        private const float CloseUpGazeSeconds = 5f;
        private const float CloseUpGazeCooldownSeconds = 30f;

        // §131: стартовый кадр игры — камера сразу у головы первой выделенной,
        // спереди-сбоку (¾), низко, и слежение уже включено. Числа под тюнинг.
        private const float OpeningShotDistance = 2.6f;
        private const float OpeningShotPitch = 14f;
        private const float OpeningShotYawFromFacing = 145f; // 180=в лицо, 90=профиль

        // §121: ручной ввод живёт рядом на той же камере и получает клик
        // первым. Ссылка ищется лениво — компонент навешивает бутстрап.
        private SimulationInputAdapter _manualInput;

        public void SetRunner(SimulationRunnerBehaviour runner)
        {
            _runner = runner;
        }

        /// <summary>
        /// Temporarily gives the primary world pointer to an editor-style tool.
        /// Camera pan/orbit/zoom remain available, while click selection, manual
        /// orders and the marquee are disabled until the same owner releases it.
        /// Multiple tools may hold the suppression independently.
        /// </summary>
        public void SetSelectionInputSuppressed(UnityEngine.Object owner, bool suppressed)
        {
            if (owner == null) return;
            if (suppressed)
            {
                _selectionInputSuppressors.Add(owner);
                CancelPointerGesture();
            }
            else
            {
                _selectionInputSuppressors.Remove(owner);
            }
        }

        public bool SelectionInputSuppressed => _selectionInputSuppressors.Count > 0;

        /// <summary>§150: overview is still the real 3D world. Hysteresis keeps
        /// decorative grass from flickering while zoom smoothing crosses 32 wu.</summary>
        public bool OverviewActive => _overviewActive;

        // Compatibility surface for the retired DistantWorldMarkersView type.
        // The panel never creates that view and zero keeps it inert if a stale
        // scene happens to instantiate one during an asset transition.
        public float OverviewBlend => 0f;
        public float FloraBlend => 0f;

        /// <summary>Current smoothed lens-to-pivot distance in world units.</summary>
        public float SmoothedDistance => _smoothedDistance;

        /// <summary>
        /// §150.1: centers the free camera on a point selected on the tactical
        /// map. The map is navigation UI, so this deliberately does not issue
        /// a movement order or alter the current NPC selection.
        /// </summary>
        public void MoveToMapPoint(Float2 point)
        {
            _mode = Mode.Free;
            _hasSmoothedTarget = false;
            SetFreePivotAt(new Vector3(point.X, 0f, point.Y));
            _pivotVelocity = Vector3.zero;
        }

        private void CancelPointerGesture()
        {
            _leftPressActive = false;
            _selectionDragging = false;
            _rightPressActive = false;
            _rightDragging = false;
        }

        private void Start()
        {
            _camera = GetComponent<Camera>();
            var palmVisibility = GetComponent<CameraPalmCrownVisibility>();
            if (palmVisibility == null)
            {
                palmVisibility = gameObject.AddComponent<CameraPalmCrownVisibility>();
            }

            palmVisibility.Construct(
                PalmCrownHideDistance,
                PalmCrownShowDistance,
                PalmCrownCheckMovement);

            var closeUpGaze = GetComponent<CameraCloseUpGaze>();
            if (closeUpGaze == null)
            {
                closeUpGaze = gameObject.AddComponent<CameraCloseUpGaze>();
            }

            closeUpGaze.Construct(
                CloseUpGazeEnterDistance,
                CloseUpGazeExitDistance,
                CloseUpGazeSeconds,
                CloseUpGazeCooldownSeconds);
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
            // ground under the camera (plus the standing-head hover, §131).
            _freePivotHeight = SimulationUnityMapper.HexRadius * _orbitNeckFactor;
            _freePivot = GroundAnchor(new Vector3(_startPosition.x, 0f, _startPosition.z));
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
            _overviewActive = false;
            _overviewImpostorsActive = false;
            _worldRenderer?.SetOverviewGrassHidden(false);
            _worldRenderer?.SetOverviewImpostorsActive(false);
        }

        // §123 / bug #348: every real selection change is camera-neutral.
        // If the old subject was followed, detach at the exact current pivot;
        // an explicit Frame emitted after SelectionChanged may still reframe.
        private void OnSelectionChanged(System.Collections.Generic.IReadOnlyList<int> selection)
        {
            _pendingFrameSelection = false;
            if (_mode == Mode.Orbit)
            {
                ExitOrbit();
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

            // Follow: повторный клик по уже выбранному персонажу —
            // камера фокусируется и следует.
            // Bug #146: скрытую туманом чужачку слежение не берёт — иначе
            // выделение через карточку отношений выдало бы её позицию (или
            // тут же молча сбросилось бы в UpdateOrbit).
            var snapshot = _runner != null && _runner.IsReady ? _runner.CreateSnapshot() : null;
            if (snapshot != null && !TryGetSelectionFrame(snapshot, out _, out _))
            {
                return;
            }

            if (_mode != Mode.Orbit)
            {
                EnterOrbitSelection();
            }

            _pendingFrameSelection = true;
        }

        private void LateUpdate()
        {
            if (_runner == null)
            {
                _runner = FindAnyObjectByType<SimulationRunnerBehaviour>();
            }

            if (_worldRenderer == null)
            {
                _worldRenderer = FindAnyObjectByType<HexWorldRenderer>();
            }

            if (UI.AdminVoicePanel.BlocksGameInput) return;
            var keyboard = Keyboard.current;
            if (!UI.AdminVoicePanel.BlocksGameInput && keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
            {
                if (UI.ContextMenuPanel.IsOpen)
                {
                    UI.ContextMenuPanel.Close();
                }
                else if (UI.LootTransferPanel.IsOpen)
                {
                    UI.LootTransferPanel.Close();
                }
                else if (_mode == Mode.Orbit)
                {
                    // Первый Escape только освобождает камеру — выделение
                    // остаётся; второй уже снимает выделение.
                    ExitOrbit();
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

            RefreshOverviewProfile();

            // Bug #279: окна закрываются на pointer-DOWN внутри Update-фазы
            // UI Toolkit, а гейт мира читается в LateUpdate — на кадре
            // закрытия PointerBlockedForWorld уже false, и тот же клик
            // становился приказом идти. Семпл В КОНЦЕ LateUpdate (после
            // гейтов этого кадра, безусловно — кадры с открытым GameMenu
            // тоже попадают в историю) даёт press-защёлкам знать, что мир
            // был закрыт кадр назад, — окно съедает свой клик целиком.
            _worldPointerBlockedLastFrame = PointerBlockedForWorld();
        }

        private void RefreshOverviewProfile()
        {
            if (_overviewActive)
            {
                if (_smoothedDistance <= _overviewShowDistance)
                {
                    _overviewActive = false;
                }
            }
            else if (_smoothedDistance >= _overviewHideDistance)
            {
                _overviewActive = true;
            }

            // §150.4: второй гистерезис — дальше 64 wu флора и лежащие вещи
            // живут world-space импосторами, меши возвращаются после 56 wu.
            if (_overviewImpostorsActive)
            {
                if (_smoothedDistance <= _overviewImpostorNearDistance)
                {
                    _overviewImpostorsActive = false;
                }
            }
            else if (_smoothedDistance >= _overviewImpostorFarDistance)
            {
                _overviewImpostorsActive = true;
            }

            // §150: distance may suppress grass for readability; impostors
            // stand in the same world anchors and use the normal ray path.
            _worldRenderer?.SetOverviewGrassHidden(_overviewActive);
            _worldRenderer?.SetOverviewImpostorsActive(_overviewImpostorsActive);
        }

        // ---- Free mode -------------------------------------------------------

        private void UpdateFree()
        {
            var snapshot = _runner != null && _runner.IsReady ? _runner.CreateSnapshot() : null;
            PruneInvisibleSelection(snapshot);
            if (_pendingFrameSelection && snapshot != null)
            {
                _pendingFrameSelection = false;
                FrameSelection(snapshot);
            }

            if (!UI.GameMenu.IsOpen && !UI.AdminVoicePanel.BlocksGameInput && !UI.EndSummaryPanel.IsOpen)
            {
                HandleFreePan();
                HandleZoom();
                HandleScrollYaw();
                HandleFreeRotation();
                HandlePointerGesture(snapshot);
            }

            _freePivot = GroundAnchor(_freePivot);

            ApplyRig(_freePivot, _panSmooth, _panSmooth);
        }

        // WASD and the arrow keys both pan. In orbit the same keys release the
        // follow first (HasPanInput) and pan from where the camera settled.
        private void HandleFreePan()
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

        // ⭐ Единица зума — НОРМИРОВАННЫЙ ЩЕЛЧОК, а не «сколько отдало устройство».
        // Unity 6 + Input System 1.19 по умолчанию держат
        // ScrollDeltaBehavior.UniformAcrossAllPlatforms, то есть приводят скролл
        // к [-1, 1] НА ВСЕХ платформах: один щелчок колеса = ровно 1.0, а не 120,
        // как Windows отдаёт в сыром виде (WHEEL_DELTA). Прежняя формула
        // (scroll * _zoomSpeed * 0.01) молча считала единицу большой и давала
        // 0.015 wu за щелчок — ~1420 щелчков уже на старом диапазоне 0.7…22 wu. На трекпаде
        // это тонуло в потоке событий и выглядело нормально, а на мыши под
        // Windows читалось как «зум почти не работает» (баг #113).
        //
        // Шаг ПРОПОРЦИОНАЛЬНЫЙ (умножение, не сложение): щелчок меняет дистанцию
        // на фиксированный процент, поэтому вблизи он мелкий, вдали крупный, и
        // ощущается одинаково в любой точке диапазона. Весь диапазон —
        // До §150-map threshold 32 wu — ~32 щелчка, до потолка 260 — ~50.
        private float ZoomedDistance(float distance, float scroll)
        {
            var ticks = Mathf.Clamp(scroll, -_maxScrollTicksPerFrame, _maxScrollTicksPerFrame);
            return Mathf.Clamp(
                distance * Mathf.Exp(-ticks * _zoomPerScrollTick),
                _orbitMinDistance, _orbitMaxDistance);
        }

        private void HandleZoom()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;

            var scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) < 0.01f) return;

            _currentDistance = ZoomedDistance(_currentDistance, scroll);
            _requestedDistance = _currentDistance;
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

        // §131: рельеф под пивотом меняется при панораме — прилипание к земле
        // сохраняет унаследованную высоту головы, а не роняет вид на грунт.
        private Vector3 GroundAnchor(Vector3 pivot)
        {
            pivot.y -= _freePivotHeight;
            pivot = SnapToGround(pivot);
            pivot.y += _freePivotHeight;
            return pivot;
        }

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
            if (SelectionInputSuppressed)
            {
                CancelPointerGesture();
                return;
            }

            var mouse = Mouse.current;
            if (mouse == null) return;

            var pointer = mouse.position.ReadValue();
            if (mouse.leftButton.wasPressedThisFrame)
            {
                // Клик мимо открытого меню только закрывает его. Клик по самому
                // меню обрабатывает UI Toolkit; в обоих случаях этот physical
                // press не имеет права стать началом выделения или приказа.
                if (UI.ContextMenuPanel.IsOpen && !UI.ContextMenuPanel.PointerOverPanel)
                {
                    UI.ContextMenuPanel.Close();
                    _leftPressActive = false;
                    _selectionDragging = false;
                    return;
                }

                _leftPressActive = !PointerBlockedForWorld() &&
                    !_worldPointerBlockedLastFrame;
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

            HandleRightClickGesture(mouse, pointer);

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

        // Правый клик без drag — контекстное меню цели. Вращение (drag) при
        // этом не страдает: жест классифицируется по порогу на отпускании.
        private void HandleRightClickGesture(Mouse mouse, Vector2 pointer)
        {
            if (mouse.rightButton.wasPressedThisFrame)
            {
                if (UI.ContextMenuPanel.IsOpen && !UI.ContextMenuPanel.PointerOverPanel)
                {
                    UI.ContextMenuPanel.Close();
                }

                _rightPressActive = !PointerBlockedForWorld() &&
                    !_worldPointerBlockedLastFrame;
                _rightDragging = false;
                _rightPressPosition = pointer;
            }

            if (_rightPressActive && mouse.rightButton.isPressed && !_rightDragging &&
                Vector2.Distance(_rightPressPosition, pointer) >= SelectionDragThresholdPixels)
            {
                _rightDragging = true;
            }

            if (!_rightPressActive || !mouse.rightButton.wasReleasedThisFrame)
            {
                return;
            }

            _rightPressActive = false;
            if (_rightDragging)
            {
                _rightDragging = false;
                return;
            }

            if (PointerBlockedForWorld())
            {
                return;
            }

            if (_manualInput == null)
            {
                _manualInput = GetComponent<SimulationInputAdapter>();
            }

            _manualInput?.TryHandleContextClick(pointer);
        }

        private static bool PointerBlockedForWorld() =>
            NpcSelection.PointerOverUi || UI.TacticalMapPanel.PointerOverMap ||
            UI.HexInspectorPanel.PointerOverPanel ||
            UI.ContextMenuPanel.BlocksWorldPointer || UI.LootTransferPanel.IsOpen ||
            UI.GameMenu.IsOpen || UI.AdminVoicePanel.BlocksGameInput ||
            UI.EndSummaryPanel.IsOpen ||
            // Bug #279: окно отчёта об ошибке держит мир закрытым само — его
            // запись в NpcSelection.PointerOverUi каждый кадр затирает
            // CharacterPanel.UpdatePointerOverUi.
            UI.BugReportPanel.IsOpen;

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
                if (_runner == null || !_runner.CanControlNpc(npc.Id) || npc.Health <= 0f)
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
            if (SelectionInputSuppressed || !_selectionDragging) return;
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
        // a selection marquee without also activating a map point.
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

        // Left-click on an NPC -> select; a repeat enters orbit mode through
        // NpcSelection's shared activation policy.
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
                    if (!CanTargetPerson(npc) ||
                        !_worldRenderer.TryGetActorView(npc.Id.Value, out var view) ||
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
            // point-radius fallback for those cases only: a person with a live
            // actor view already had the exact geometry ray above, and stretching
            // her another 70 px around the feet is exactly the "huge click"
            // that swallowed items and neighbours (§121.1 r2).
            var bestId = -1;
            var bestDist = _pickRadiusPixels;

            foreach (var npc in PickablePeople(snapshot))
            {
                if (!CanTargetPerson(npc) || (_worldRenderer != null &&
                    _worldRenderer.TryGetActorView(npc.Id.Value, out _)))
                {
                    continue;
                }

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

        private bool ApplyNpcPick(WorldSnapshot snapshot, int npcId, bool additiveOnly)
        {
            foreach (var npc in snapshot.Npcs)
            {
                if (npc.Id.Value != npcId) continue;
                var shift = Keyboard.current != null &&
                    (Keyboard.current.leftShiftKey.isPressed || Keyboard.current.rightShiftKey.isPressed);
                if ((additiveOnly || shift) &&
                    _runner != null && _runner.CanControlNpc(npc.Id) && npc.Health > 0f)
                {
                    NpcSelection.Toggle(npcId, requestFrame: false);
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

        private bool CanTargetPerson(NpcSnapshot person)
        {
            var owned = _runner != null && _runner.CanControlNpc(person.Id);
            if (owned)
            {
                return true;
            }

            if (_worldRenderer == null)
            {
                _worldRenderer = FindAnyObjectByType<HexWorldRenderer>();
            }

            return _worldRenderer != null && _worldRenderer.IsNpcPickable(
                person.Id.Value, person.Tile, false);
        }

        // §150: a contact leaving the player's perception disappears from the
        // roster and from selection in the same tick. If it was the only
        // follow target, SelectionChanged runs ExitOrbit and preserves the
        // last smoothed pivot (§131.2).
        private void PruneInvisibleSelection(WorldSnapshot snapshot)
        {
            if (snapshot == null || !NpcSelection.HasSelection)
            {
                return;
            }

            _visibleSelectionScratch.Clear();
            var ids = NpcSelection.SelectedIds;
            for (var i = 0; i < ids.Count; i++)
            {
                var id = ids[i];
                var found = false;
                foreach (var npc in snapshot.Npcs)
                {
                    if (npc.Id.Value != id)
                    {
                        continue;
                    }

                    found = CanTargetPerson(npc);
                    break;
                }

                if (!found)
                {
                    foreach (var corpse in snapshot.Corpses)
                    {
                        if (corpse.Id.Value != id)
                        {
                            continue;
                        }

                        found = CanTargetPerson(corpse);
                        break;
                    }
                }

                if (found)
                {
                    _visibleSelectionScratch.Add(id);
                }
            }

            if (_visibleSelectionScratch.Count != ids.Count)
            {
                NpcSelection.ReplaceMany(_visibleSelectionScratch, requestFrame: false);
            }
        }

        public bool TryGetAdminCameraContext(out HexLive.Simulation.Common.TileCoord ground, out Vector3 position, out Vector3 forward, out Vector3 groundPoint)
        {
            _camera ??= GetComponent<Camera>();
            position = _camera != null ? _camera.transform.position : Vector3.zero;
            forward = _camera != null ? _camera.transform.forward : Vector3.forward;
            return TryPickHex(new Vector2(Screen.width * 0.5f, Screen.height * 0.5f), out ground, out groundPoint, null);
        }

        private bool TryPickHex(
            Vector2 mousePos,
            out HexLive.Simulation.Common.TileCoord coord,
            WorldSnapshot currentSnapshot = null)
            => TryPickHex(mousePos, out coord, out _, currentSnapshot);

        private bool TryPickHex(Vector2 mousePos, out HexLive.Simulation.Common.TileCoord coord,
            out Vector3 groundPoint, WorldSnapshot currentSnapshot)
        {
            coord = default;
            groundPoint = default;

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
                groundPoint = hit;
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

            // §131: центр кадра приходит уже на высоте голов (TryGetOrbitTarget)
            // — свободный пивот принимает эту высоту, а не падает на землю.
            SetFreePivotAt(center);
            var fit = FitDistance(radius);
            _requestedDistance = fit;
            _currentDistance = fit;
            _pivotVelocity = Vector3.zero;
        }

        // §131: поставить свободный пивот в точку, запомнив её высоту над
        // землёй — единственный способ записи _freePivot из мира NPC.
        private void SetFreePivotAt(Vector3 point)
        {
            var ground = SnapToGround(new Vector3(point.x, 0f, point.z));
            _freePivotHeight = Mathf.Clamp(
                point.y - ground.y, 0f, SimulationUnityMapper.HexRadius);
            _freePivot = ground + Vector3.up * _freePivotHeight;
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
        /// §131: openingShot=true — стартовый кадр: слежение за primary уже
        /// включено, камера близко, спереди-сбоку и низко (константы
        /// OpeningShot*).
        /// </summary>
        public bool SnapToSelectedTarget(bool openingShot = false)
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

            _camera ??= GetComponent<Camera>();
            var pivot = target;
            if (openingShot &&
                TryGetOrbitTarget(snapshot, NpcSelection.PrimaryId, out var anchor))
            {
                _mode = Mode.Orbit;
                pivot = anchor;
                _smoothedTarget = anchor;
                _hasSmoothedTarget = true;
                _requestedDistance = Mathf.Clamp(
                    OpeningShotDistance, _orbitMinDistance, _orbitMaxDistance);
                _currentPitch = Mathf.Clamp(
                    OpeningShotPitch, _orbitMinPitch, _orbitMaxPitch);
                foreach (var npc in snapshot.Npcs)
                {
                    if (npc.Id.Value != NpcSelection.PrimaryId)
                    {
                        continue;
                    }

                    _currentYaw = SimulationUnityMapper.ToUnityYawDegrees(
                        npc.RotationDegrees) + OpeningShotYawFromFacing;
                    break;
                }
            }
            else
            {
                _mode = Mode.Free;
                _requestedDistance = FitDistance(radius);
            }

            SetFreePivotAt(pivot);
            _currentDistance = _requestedDistance;
            _smoothedYaw = _currentYaw;
            _smoothedPitch = _currentPitch;
            _yawVelocity = 0f;
            _pitchVelocity = 0f;
            // Snap every smoothed parameter: the next LateUpdate must
            // reproduce this exact pose with zero first-frame glide.
            _smoothedPivot = pivot;
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
                pivot - rotation * Vector3.forward * _currentDistance -
                rotation * Vector3.up * uiLift + rotation * Vector3.right * uiSide,
                rotation);
            return true;
        }

        private void ExitOrbit()
        {
            _mode = Mode.Free;
            // §131: отцепили слежение — камера ОСТАЁТСЯ ровно где была: пивот
            // принимает фактическое (сглаженное) положение рига вместе с его
            // высотой над землёй, дальше NPC сам уходит из кадра. Раньше тут
            // стоял SnapToGround и вид ронялся с головы на ноги. Угол,
            // дистанция и кадрирование не трогаются; _orbitDistance NOT
            // updated here: FitDistance floors explicit Frame requests with it,
            // and mutating it would turn the last free-camera height into a
            // creeping zoom minimum.
            _requestedDistance = _currentDistance;
            var pivot = _hasSmoothedPivot ? _smoothedPivot
                : _hasSmoothedTarget ? _smoothedTarget : transform.position;
            SetFreePivotAt(pivot);
            _pivotVelocity = Vector3.zero;
        }

        private void UpdateOrbit()
        {
            var snapshot = _runner != null && _runner.IsReady ? _runner.CreateSnapshot() : null;
            if (snapshot == null)
            {
                return;
            }

            PruneInvisibleSelection(snapshot);
            if (_mode != Mode.Orbit)
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
            // A world click is handled inside UpdateOrbit itself. Selecting a
            // different NPC fires OnSelectionChanged -> ExitOrbit; do not let
            // the remainder of this old orbit tick pull once toward the new
            // subject before free mode takes over next frame (bug #348).
            if (_mode != Mode.Orbit)
            {
                return;
            }

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
            HandleScrollYaw();

            _currentDistance = Mathf.Clamp(
                _requestedDistance, _orbitMinDistance, _orbitMaxDistance);

            _currentPitch = Mathf.Clamp(_currentPitch, _orbitMinPitch, _orbitMaxPitch);
            ApplyRig(_smoothedTarget, _orbitTargetSmooth, _orbitPositionSmooth);
        }

        // Стрелки — тоже панорама: в режиме слежения любой pan-ввод отцепляет
        // камеру, дальше она свободно едет, как после Escape.
        private static bool HasPanInput()
        {
            var keyboard = Keyboard.current;
            return keyboard != null && (keyboard.wKey.isPressed || keyboard.aKey.isPressed ||
                keyboard.sKey.isPressed || keyboard.dKey.isPressed ||
                keyboard.upArrowKey.isPressed || keyboard.downArrowKey.isPressed ||
                keyboard.leftArrowKey.isPressed || keyboard.rightArrowKey.isPressed);
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
                // Тот же пропорциональный шаг, что и в свободном режиме: зум
                // обязан ощущаться одинаково по обе стороны переключения follow.
                _requestedDistance = ZoomedDistance(_requestedDistance, scroll);
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

        // The orbit pivot follows the actor root, not animated hip/head bones.
        // Otherwise breathing and transitions into sitting/lying move the
        // camera target even while the NPC has not moved in the world, which
        // reads as framing being reset. The canonical neck offset is stable for
        // every pose; snapshot position remains the loading fallback.
        private bool TryGetOrbitTarget(
            HexLive.Simulation.Debug.WorldSnapshot snapshot, int npcId, out Vector3 target)
        {
            var neck = SimulationUnityMapper.HexRadius * _orbitNeckFactor;

            if (_worldRenderer == null)
            {
                _worldRenderer = FindAnyObjectByType<HexWorldRenderer>();
            }

            foreach (var npc in snapshot.Npcs)
            {
                if (npc.Id.Value == npcId && !CanTargetPerson(npc))
                {
                    target = Vector3.zero;
                    return false;
                }
            }

            foreach (var corpse in snapshot.Corpses)
            {
                if (corpse.Id.Value == npcId && !CanTargetPerson(corpse))
                {
                    target = Vector3.zero;
                    return false;
                }
            }

            // Bug #146: selecting a hidden outsider through a relationship
            // card must not leak her location by framing an inactive actor or
            // falling back to the authoritative snapshot position.
            if (_worldRenderer != null && _worldRenderer.IsNpcHiddenByFog(npcId))
            {
                target = Vector3.zero;
                return false;
            }

            if (_worldRenderer != null && _worldRenderer.TryGetNpcViewPosition(npcId, out var viewPos))
            {
                // §131: высоту головы отвечает сама фигура ЕДИНЫМ методом —
                // стоит/сидит/лежит/плывёт даёт свою высоту, камера не гадает.
                var height = _worldRenderer.TryGetActorView(npcId, out var actorView)
                    ? actorView.CameraAnchorHeight
                    : neck;
                target = viewPos + Vector3.up * height;
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
