using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Spatial;
using UnityEngine;
using UnityEngine.InputSystem;

namespace HexLive.UnityPresentation.Input
{
    [DisallowMultipleComponent]
    public sealed class OrbitCameraController : MonoBehaviour
    {
        [SerializeField] private SimulationRunnerBehaviour _runner;
        [SerializeField] private float _distance = 9f;
        [SerializeField] private float _minDistance = 4f;
        [SerializeField] private float _maxDistance = 16f;
        [SerializeField] private float _zoomSpeed = 1.5f;
        [SerializeField] private float _rotationSpeed = 3.5f;
        [SerializeField] private float _positionSmoothTime = 0.08f;
        [SerializeField] private float _rotationSmoothTime = 0.06f;
        [SerializeField] private float _pitch = 28f;
        [SerializeField] private float _minPitch = 10f;
        [SerializeField] private float _maxPitch = 70f;
        [SerializeField] private float _yaw = 35f;

        private Vector3 _currentVelocity;
        private float _yawVelocity;
        private float _pitchVelocity;
        private float _smoothedYaw;
        private float _smoothedPitch;
        private bool _initialized;

        public void SetRunner(SimulationRunnerBehaviour runner)
        {
            _runner = runner;
        }

        private void LateUpdate()
        {
            if (_runner == null)
            {
                _runner = FindFirstObjectByType<SimulationRunnerBehaviour>();
            }

            if (_runner == null || !_runner.IsReady)
            {
                return;
            }

            var snapshot = _runner.CreateSnapshot();
            if (snapshot == null || snapshot.Npcs.Count == 0)
            {
                return;
            }

            HandleInput();

            if (!_initialized)
            {
                _smoothedYaw = _yaw;
                _smoothedPitch = _pitch;
                _initialized = true;
            }

            _smoothedYaw = Mathf.SmoothDampAngle(_smoothedYaw, _yaw, ref _yawVelocity, _rotationSmoothTime);
            _smoothedPitch = Mathf.SmoothDampAngle(_smoothedPitch, _pitch, ref _pitchVelocity, _rotationSmoothTime);

            var npc = snapshot.Npcs[0];
            var target = SimulationUnityMapper.ToUnityPosition(
                npc.Position,
                SimulationUnityMapper.CameraTargetHeight);
            var rotation = Quaternion.Euler(_smoothedPitch, _smoothedYaw, 0f);
            var desiredPosition = target - rotation * Vector3.forward * _distance;

            transform.position = Vector3.SmoothDamp(transform.position, desiredPosition, ref _currentVelocity, _positionSmoothTime);
            transform.rotation = rotation;
        }

        private void HandleInput()
        {
            var mouse = Mouse.current;
            if (mouse == null)
            {
                return;
            }

            var scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.001f)
            {
                _distance = Mathf.Clamp(_distance - scroll * (_zoomSpeed * 0.01f), _minDistance, _maxDistance);
            }

            if (!mouse.rightButton.isPressed)
            {
                return;
            }

            var delta = mouse.delta.ReadValue();
            _yaw += delta.x * (_rotationSpeed * Time.unscaledDeltaTime);
            _pitch -= delta.y * (_rotationSpeed * Time.unscaledDeltaTime);
            _pitch = Mathf.Clamp(_pitch, _minPitch, _maxPitch);
        }
    }
}
