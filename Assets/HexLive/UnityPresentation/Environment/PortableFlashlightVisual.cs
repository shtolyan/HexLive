using HexLive.UnityPresentation.Wearing;
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>§176. A cool spotlight aligned with the imported lens, using world-space range.</summary>
    public sealed class PortableFlashlightVisual : MonoBehaviour
    {
        private Transform _lens;
        private Transform _tail;
        private Transform _actor;
        private Light _beam;

        public static PortableFlashlightVisual Attach(GameObject model, bool held = false)
        {
            Transform lens = null;
            Transform tail = null;
            foreach (var marker in model.GetComponentsInChildren<Transform>(true))
            {
                if (marker.name == "FlashlightLens") lens = marker;
                if (marker.name == "FlashlightTail") tail = marker;
            }
            if (lens == null || tail == null) return null;

            var visual = model.AddComponent<PortableFlashlightVisual>();
            visual._lens = lens;
            visual._tail = tail;
            if (held && model.GetComponentInParent<NpcActorView>() is { } actor)
                visual._actor = actor.transform;

            var beam = new GameObject("Flashlight beam").AddComponent<Light>();
            beam.transform.SetParent(model.transform, false);
            beam.type = LightType.Spot;
            beam.color = new Color(.85f, .91f, 1f);
            beam.range = 7.5f;
            beam.intensity = 4.2f;
            beam.spotAngle = 48f;
            beam.innerSpotAngle = 25f;
            beam.shadows = LightShadows.None;
            beam.enabled = false;
            visual._beam = beam;
            visual.SetLit(held);
            return visual;
        }

        public void SetLit(bool lit)
        {
            if (lit && !_beam.enabled) AlignBeam();
            _beam.enabled = lit;
        }

        private void LateUpdate()
        {
            if (_beam.enabled) AlignBeam();
        }

        private void AlignBeam()
        {
            var direction = (_lens.position - _tail.position).normalized;
            if (_actor != null)
            {
                var aim = (_actor.forward + Vector3.down * .12f).normalized;
                transform.rotation = Quaternion.FromToRotation(direction, aim) * transform.rotation;
                direction = (_lens.position - _tail.position).normalized;
            }
            if (direction.sqrMagnitude < .01f) return;
            _beam.transform.SetPositionAndRotation(_lens.position + direction * .004f,
                Quaternion.LookRotation(direction));
        }
    }
}
