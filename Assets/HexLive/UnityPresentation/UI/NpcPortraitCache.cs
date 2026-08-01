using System.Collections.Generic;
using HexLive.UnityPresentation.Rendering;
using UnityEngine;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// §80: снимки лиц. Раз в игровой час камера фотографирует одного NPC в
    /// текстуру, и этот снимок потом показывают везде, где раньше был цветной
    /// кружок с буквой: пузырь «кого боюсь», вкладка отношений.
    ///
    /// Снимок, а НЕ живой рендер — по трём причинам, каждая из которых сама по
    /// себе решает вопрос:
    ///  * лиц нужно много сразу (в отношениях их столько, сколько знакомых), а
    ///    живая камера на каждого — это по камере на лицо каждый кадр;
    ///  * снимок переживает смерть персонажа: вью уничтожается, а лицо в списке
    ///    отношений должно остаться;
    ///  * SpriteRenderer пузыря умеет Sprite, а Sprite.Create не принимает
    ///    RenderTexture — нужен именно Texture2D.
    ///
    /// Кадрирование взято у PortraitStage дословно: камера висит перед лицом по
    /// СОБСТВЕННЫМ осям лицевого рига, поэтому голова может вертеться сколько
    /// угодно (LookAtIK её и вертит), а лицо в кадре стоит ровно.
    /// </summary>
    public sealed class NpcPortraitCache : MonoBehaviour
    {
        // Портрет живёт в пузыре размером ~0.3 мировых единицы и в кружке 31-74
        // пикселя. 192 хватает с запасом и на будущее, а весь кэш на дюжину тел
        // — меньше 2 МБ.
        private const int TextureSize = 192;

        // Те же числа, что в PortraitStage: подобраны на модели ростом 1.7 м и
        // домножаются на мировой масштаб.
        private const float FaceDistanceMeters = 0.72f;
        private const float EyeLiftMeters = 0.03f;

        private static readonly Color Backdrop = new(0.10f, 0.12f, 0.14f, 1f);

        private RenderTexture _scratch;
        private Camera _camera;
        private HexWorldRenderer _worldRenderer;
        private bool _maskResolved;

        private readonly Dictionary<int, Texture2D> _portraits = new();
        private readonly Dictionary<int, Sprite> _sprites = new();
        private readonly Dictionary<int, int> _bakedAtTick = new();

        // Съёмка занимает ДВА кадра: в первом камеру наводят и включают, во
        // втором читают уже отрисованный кадр. Так делают оба существующих
        // стейджа, и так надо: camera.Render() вызывается в обход порядка URP.
        private int _pendingNpcId = -1;

        private int _lastSweepTick = int.MinValue;

        /// <summary>Готовый снимок лица, если он уже сделан.</summary>
        public bool TryGet(int npcId, out Texture2D portrait)
        {
            return _portraits.TryGetValue(npcId, out portrait) && portrait != null;
        }

        /// <summary>Тот же снимок спрайтом — для пузыря над головой.</summary>
        public Sprite SpriteFor(int npcId)
        {
            if (_sprites.TryGetValue(npcId, out var cached) && cached != null)
            {
                return cached;
            }

            if (!TryGet(npcId, out var texture))
            {
                return null;
            }

            var sprite = Sprite.Create(
                texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f),
                texture.width);
            _sprites[npcId] = sprite;
            return sprite;
        }

        /// <summary>
        /// Снять лицо вне очереди — над головой вот-вот всплывёт пузырь, а
        /// снимка этого NPC ещё нет (первый час игры).
        /// </summary>
        public void RequestNow(int npcId)
        {
            if (npcId < 0 || _portraits.ContainsKey(npcId) || _pendingNpcId >= 0)
            {
                return;
            }

            _pendingNpcId = npcId;
        }

        /// <summary>
        /// Раз в игровой час снять самое несвежее лицо. Один NPC за проход:
        /// съёмка — это кадр камеры плюс чтение из GPU, и растягивать её по
        /// одному телу дешевле, чем снимать всех разом.
        /// </summary>
        public void Sweep(int tick, int dayLengthTicks, IReadOnlyList<int> npcIds)
        {
            if (npcIds == null || npcIds.Count == 0)
            {
                return;
            }

            var hourTicks = Mathf.Max(1, dayLengthTicks / 24);
            if (_lastSweepTick != int.MinValue && tick - _lastSweepTick < hourTicks)
            {
                return;
            }

            _lastSweepTick = tick;
            if (_pendingNpcId >= 0)
            {
                return;
            }

            var stalest = -1;
            var stalestTick = int.MaxValue;
            foreach (var id in npcIds)
            {
                var baked = _bakedAtTick.TryGetValue(id, out var at) ? at : int.MinValue;
                if (baked < stalestTick)
                {
                    stalestTick = baked;
                    stalest = id;
                }
            }

            _pendingNpcId = stalest;
        }

        private void Awake()
        {
            _scratch = new RenderTexture(TextureSize, TextureSize, 16, RenderTextureFormat.ARGB32)
            {
                name = "NpcPortraitScratch",
                antiAliasing = 2
            };
            _scratch.Create();

            var camGo = new GameObject("PortraitBakeCamera");
            camGo.transform.SetParent(transform, false);

            _camera = camGo.AddComponent<Camera>();
            _camera.targetTexture = _scratch;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = Backdrop;
            _camera.fieldOfView = 22f;
            _camera.nearClipPlane = 0.03f;
            _camera.farClipPlane = 60f;
            _camera.cullingMask = ~(1 << 5);
            _camera.enabled = false;
        }

        private void LateUpdate()
        {
            if (_camera == null)
            {
                return;
            }

            // Только слой Actors: персонаж со всей его грязью, загаром, ранами и
            // одеждой на чистом фоне, без окружения.
            if (!_maskResolved)
            {
                var actorsLayer = LayerMask.NameToLayer("Actors");
                if (actorsLayer >= 0)
                {
                    _camera.cullingMask = 1 << actorsLayer;
                    _maskResolved = true;
                }
            }

            // Кадр 2: камера уже отрисовала кого просили — забрать пиксели.
            if (_camera.enabled)
            {
                Capture(_pendingNpcId);
                _camera.enabled = false;
                _pendingNpcId = -1;
                return;
            }

            if (_pendingNpcId < 0)
            {
                return;
            }

            // Кадр 1: навести и включить.
            if (_worldRenderer == null)
            {
                _worldRenderer = FindAnyObjectByType<HexWorldRenderer>();
                if (_worldRenderer == null)
                {
                    return;
                }
            }

            if (!_worldRenderer.TryGetNpcFace(
                    _pendingNpcId, out var face, out var forward, out var up, out var scale))
            {
                // Тела ещё нет в мире (или уже нет) — попробуем в следующий час.
                _pendingNpcId = -1;
                return;
            }

            var eye = face + forward * (FaceDistanceMeters * scale) + up * (EyeLiftMeters * scale);
            _camera.transform.position = eye;
            _camera.transform.rotation = Quaternion.LookRotation(face - eye, up);
            _camera.enabled = true;
        }

        private void Capture(int npcId)
        {
            if (npcId < 0)
            {
                return;
            }

            if (!_portraits.TryGetValue(npcId, out var texture) || texture == null)
            {
                texture = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, false)
                {
                    name = $"NpcPortrait{npcId}",
                    wrapMode = TextureWrapMode.Clamp
                };
                _portraits[npcId] = texture;
            }

            var previous = RenderTexture.active;
            RenderTexture.active = _scratch;
            texture.ReadPixels(new Rect(0f, 0f, TextureSize, TextureSize), 0, 0, false);
            texture.Apply(false);
            RenderTexture.active = previous;

            _bakedAtTick[npcId] = _lastSweepTick;

            // Спрайт держит ссылку на текстуру, а не копию, но пересоздать его
            // всё равно надо: Sprite кэширует размеры на момент создания.
            if (_sprites.TryGetValue(npcId, out var stale) && stale != null)
            {
                Destroy(stale);
                _sprites.Remove(npcId);
            }
        }

        private void OnDestroy()
        {
            if (_camera != null)
            {
                _camera.targetTexture = null;
            }

            if (_scratch != null)
            {
                _scratch.Release();
                Destroy(_scratch);
            }

            foreach (var sprite in _sprites.Values)
            {
                if (sprite != null)
                {
                    Destroy(sprite);
                }
            }

            foreach (var texture in _portraits.Values)
            {
                if (texture != null)
                {
                    Destroy(texture);
                }
            }

            _sprites.Clear();
            _portraits.Clear();
        }
    }
}
