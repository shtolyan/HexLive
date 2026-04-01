using UnityEngine;
using UnityEngine.InputSystem;

namespace HexLive.UnityPresentation.Input
{
    /// <summary>
    /// RTS-style camera: fixed top-down angle, WASD/arrows to pan, scroll to zoom.
    /// No rotation. No edge pan.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RtsCameraController : MonoBehaviour
    {
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

        private Vector3 _targetPosition;
        private float _targetHeight;
        private Vector3 _velocity;

        private void Start()
        {
            transform.position = _startPosition;
            transform.rotation = Quaternion.Euler(_startRotation);
            _targetPosition = _startPosition;
            _targetHeight = _startPosition.y;
        }

        private void LateUpdate()
        {
            HandlePan();
            HandleZoom();
            ApplyMovement();
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

            _targetPosition.x += input.x * speed;
            _targetPosition.z += input.y * speed;
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

        private void ApplyMovement()
        {
            var desired = new Vector3(_targetPosition.x, _targetHeight, _targetPosition.z);
            transform.position = Vector3.SmoothDamp(transform.position, desired, ref _velocity, _panSmooth);
            transform.rotation = Quaternion.Euler(_startRotation);
        }
    }
}
