#nullable enable
using HexLive.UnityPresentation.Environment;
using UnityEngine;

namespace HexLive.UnityPresentation.Rendering
{
    /// <summary>
    /// Spec §112: one camera-side pass decides which standing palm crowns are
    /// close enough to become shadow-only. Distance is measured from the lens
    /// to the LeafGreen submesh bounds in full 3D, so a high camera keeps the
    /// foliage visible.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    [DefaultExecutionOrder(310)]
    public sealed class CameraPalmCrownVisibility : MonoBehaviour
    {
        private float _hideDistance;
        private float _showDistance;
        private float _minimumMovementSqr;
        private Vector3 _lastCheckedPosition;
        private int _lastRegistryVersion = -1;
        private bool _hasCheckedPosition;

        public void Construct(
            float hideDistance,
            float showDistance,
            float minimumMovement)
        {
            _hideDistance = Mathf.Max(0f, hideDistance);
            _showDistance = Mathf.Max(_hideDistance, showDistance);
            var clampedMovement = Mathf.Max(0f, minimumMovement);
            _minimumMovementSqr = clampedMovement * clampedMovement;
            _hasCheckedPosition = false;

            if (isActiveAndEnabled)
            {
                Evaluate(force: true);
            }
        }

        private void OnEnable()
        {
            _hasCheckedPosition = false;
        }

        private void LateUpdate()
        {
            // Runs after RtsCameraController moved the lens.
            Evaluate(force: false);
        }

        private void OnDisable()
        {
            StandingPalmCrownVisibility.ShowAllRegistered();
            _hasCheckedPosition = false;
            _lastRegistryVersion = -1;
        }

        private void Evaluate(bool force)
        {
            var cameraPosition = transform.position;
            var registryVersion = StandingPalmCrownVisibility.RegistryVersion;
            if (!force &&
                _hasCheckedPosition &&
                registryVersion == _lastRegistryVersion &&
                (cameraPosition - _lastCheckedPosition).sqrMagnitude < _minimumMovementSqr)
            {
                return;
            }

            _lastCheckedPosition = cameraPosition;
            _lastRegistryVersion = registryVersion;
            _hasCheckedPosition = true;

            var crowns = StandingPalmCrownVisibility.ActiveCrowns;
            for (var i = 0; i < crowns.Count; i++)
            {
                var crown = crowns[i];
                if (crown == null || !crown.TryGetWorldCrownBounds(out var bounds))
                {
                    continue;
                }

                var threshold = crown.IsHidden ? _showDistance : _hideDistance;
                var shouldHide = bounds.SqrDistance(cameraPosition) <= threshold * threshold;
                crown.SetHidden(shouldHide);
            }
        }
    }
}
