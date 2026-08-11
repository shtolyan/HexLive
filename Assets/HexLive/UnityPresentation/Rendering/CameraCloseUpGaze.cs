#nullable enable
using System.Collections.Generic;
using HexLive.UnityPresentation.Wearing;
using UnityEngine;

namespace HexLive.UnityPresentation.Rendering
{
    /// <summary>
    /// §130: камера подъехала почти вплотную к лицу NPC — она на несколько
    /// секунд поднимает взгляд в объектив и чуть улыбается, потом
    /// возвращается к своей обычной жизни. Один взгляд на один «подъезд»:
    /// пере-взвод требует выйти из зоны, плюс пер-NPC кулдаун. Пороги
    /// вход/выход с гистерезисом, как у CameraPalmCrownVisibility (§112).
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    [DefaultExecutionOrder(310)]
    public sealed class CameraCloseUpGaze : MonoBehaviour
    {
        private float _enterDistance;
        private float _exitDistance;
        private float _gazeSeconds;
        private float _cooldownSeconds;

        private HexWorldRenderer? _renderer;
        private NpcActorView? _activeView;
        private int _activeId = -1;
        // Один взгляд на один подъезд камеры: после срабатывания ждём, пока
        // зона опустеет, и только потом взводимся снова.
        private bool _armed = true;
        private readonly Dictionary<int, float> _cooldownUntil = new();

        public void Construct(
            float enterDistance,
            float exitDistance,
            float gazeSeconds,
            float cooldownSeconds)
        {
            _enterDistance = Mathf.Max(0f, enterDistance);
            _exitDistance = Mathf.Max(_enterDistance, exitDistance);
            _gazeSeconds = Mathf.Max(0f, gazeSeconds);
            _cooldownSeconds = Mathf.Max(0f, cooldownSeconds);
        }

        private void OnDisable()
        {
            if (_activeView != null)
            {
                _activeView.EndCameraGaze();
            }

            _activeView = null;
            _activeId = -1;
            _armed = true;
        }

        private void LateUpdate()
        {
            // Runs after RtsCameraController moved the lens.
            if (_renderer == null)
            {
                _renderer = FindAnyObjectByType<HexWorldRenderer>();
                if (_renderer == null)
                {
                    return;
                }
            }

            var lens = transform.position;

            if (_activeView != null)
            {
                // Unity-null = вью уничтожено (смерть/деспавн) — просто
                // отпускаем без кулдауна.
                if (!_activeView.HasCameraGaze)
                {
                    // Кончился по таймеру (или его погасили ragdoll/портрет).
                    ReleaseActive(withCooldown: true);
                }
                else if (!IsWithin(_activeView, lens, _exitDistance))
                {
                    _activeView.EndCameraGaze();
                    ReleaseActive(withCooldown: true);
                }
                else
                {
                    _activeView.UpdateCameraGaze(lens);
                    return;
                }
            }
            else if (_activeId >= 0)
            {
                ReleaseActive(withCooldown: false);
            }

            if (!_renderer.TryGetNearestCameraGazeCandidate(
                    lens, out var npcId, out var view, out var faceCenter) ||
                view == null)
            {
                _armed = true;
                return;
            }

            if ((faceCenter - lens).sqrMagnitude > _enterDistance * _enterDistance)
            {
                _armed = true;
                return;
            }

            if (!_armed ||
                (_cooldownUntil.TryGetValue(npcId, out var until) && Time.time < until))
            {
                return;
            }

            if (view.BeginCameraGaze(lens, _gazeSeconds))
            {
                _activeView = view;
                _activeId = npcId;
                _armed = false;
            }
        }

        private void ReleaseActive(bool withCooldown)
        {
            if (withCooldown && _activeId >= 0)
            {
                _cooldownUntil[_activeId] = Time.time + _cooldownSeconds;
            }

            _activeView = null;
            _activeId = -1;
        }

        private static bool IsWithin(NpcActorView view, Vector3 lens, float distance)
        {
            if (!view.TryGetFace(out var center, out _, out _, out _))
            {
                return false;
            }

            return (center - lens).sqrMagnitude <= distance * distance;
        }
    }
}
