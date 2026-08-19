#nullable enable
using UnityEngine;

namespace HexLive.UnityPresentation.Views
{

/// <summary>
/// §148: держит объект лицом к камере. Заведено для «?» — метки чужого
/// человека, которого наши видели, но сейчас не видят: без разворота она
/// читалась бы боком под RTS-углом.
/// <para>
/// Отдельным компонентом, а не строкой в рендерере: разворот нужен КАЖДЫЙ
/// кадр, а рендерер обновляется по тику — метка иначе дёргалась бы при
/// вращении камеры.
/// </para>
/// </summary>
public sealed class CameraBillboard : MonoBehaviour
{
    private Camera? _camera;

    private void LateUpdate()
    {
        if (_camera == null)
        {
            _camera = Camera.main;
            if (_camera == null)
            {
                return;
            }
        }

        transform.rotation = _camera.transform.rotation;
    }
}

}
