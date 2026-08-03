using System.Collections.Generic;
using HexLive.UnityPresentation.Rendering;
using UnityEngine;

namespace HexLive.UnityPresentation.UI
{
    /// <summary>
    /// §80: снимки лиц. Раз в игровые СУТКИ, днём, камера фотографирует
    /// каждого NPC в текстуру, и этот снимок потом показывают везде, где
    /// раньше был цветной кружок с буквой: пузырь «кого боюсь», вкладка
    /// отношений.
    ///
    /// Это именно фотография, а не случайный кадр: на время съёмки колонистка
    /// смотрит В КАМЕРУ (BeginPortraitGaze) и не моргает, фон прозрачный, свет
    /// дневной. Раньше снимали каждый час чем придётся — выходил тёмный
    /// профиль с закрытыми глазами на серой плашке.
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

        // Фон ПРОЗРАЧНЫЙ: снимок — вырезка персонажа, а не плашка. Тёмную
        // подложку под неё рисует та панель, которой она нужна.
        private static readonly Color Backdrop = new(0.10f, 0.12f, 0.14f, 0f);

        // Снимать при дневном свете: ночью колонистка — тёмный силуэт, и
        // фотография ни на что не годится. Окно 10:00-16:00 (0 = 06:00).
        private const float DaylightFrom = 4f / 24f;
        private const float DaylightTo = 10f / 24f;

        // Кадров на «посмотри в камеру», прежде чем нажать затвор: взгляд
        // ведёт Final-IK, и мгновенно он не доезжает.
        private const int GazeConvergeFrames = 4;

        // Между снимками — пауза в реальном времени: за один дневной оконный
        // проход надо снять всех, но не десятком ReadPixels в одном кадре.
        private const float BakeSpacingSeconds = 0.75f;

        private RenderTexture _scratch;
        private Camera _camera;
        private HexWorldRenderer _worldRenderer;
        private bool _maskResolved;

        private readonly Dictionary<int, Texture2D> _portraits = new();
        private readonly Dictionary<int, Sprite> _sprites = new();
        private readonly Dictionary<int, int> _bakedAtTick = new();
        private readonly Dictionary<int, int> _bakedDay = new();

        // Съёмка идёт несколько кадров: навести и заморозить камеру → дать
        // взгляду доехать до объектива → отрисовать → прочитать пиксели.
        // camera.Render() в обход порядка URP по-прежнему нельзя.
        private enum BakePhase
        {
            Idle,
            Converge,
            Rendering
        }

        private BakePhase _phase = BakePhase.Idle;
        private int _pendingNpcId = -1;
        private int _convergeFrames;
        private bool _gazeHeld;
        private float _nextBakeTime;

        private int _lastSweepTick = int.MinValue;
        private int _currentDay;

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
        /// Фотосессия раз в СУТКИ и только при дневном свете. Один NPC за
        /// проход: съёмка — это кадр камеры плюс чтение из GPU, и растянуть её
        /// по одному телу дешевле, чем снимать всех разом.
        /// </summary>
        public void Sweep(int tick, int dayLengthTicks, float timeOfDay01, IReadOnlyList<int> npcIds)
        {
            if (npcIds == null || npcIds.Count == 0)
            {
                return;
            }

            _lastSweepTick = tick;
            _currentDay = tick / Mathf.Max(1, dayLengthTicks);

            if (_pendingNpcId >= 0 ||
                timeOfDay01 < DaylightFrom || timeOfDay01 > DaylightTo ||
                Time.unscaledTime < _nextBakeTime)
            {
                return;
            }

            // Самая несвежая из тех, кого сегодня ещё не снимали.
            var stalest = -1;
            var stalestTick = int.MaxValue;
            foreach (var id in npcIds)
            {
                if (_bakedDay.TryGetValue(id, out var day) && day == _currentDay)
                {
                    continue;
                }

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

            switch (_phase)
            {
                // Затвор: камера уже отрисовала кого просили — забрать пиксели.
                case BakePhase.Rendering:
                    Capture(_pendingNpcId);
                    _camera.enabled = false;
                    FinishBake();
                    return;

                // Взгляд доезжает до объектива, камера при этом СТОИТ там, куда
                // её навели: она висит на осях кости головы, и если её двигать
                // вслед за поворотом, она будет гнаться сама за собой.
                case BakePhase.Converge:
                    if (--_convergeFrames > 0)
                    {
                        return;
                    }

                    _camera.enabled = true;
                    _phase = BakePhase.Rendering;
                    return;
            }

            if (_pendingNpcId < 0)
            {
                return;
            }

            // Навести, заморозить, попросить посмотреть в камеру.
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
                // Тела ещё нет в мире (или уже нет) — попробуем в следующий раз.
                FinishBake();
                return;
            }

            var eye = face + forward * (FaceDistanceMeters * scale) + up * (EyeLiftMeters * scale);
            _camera.transform.position = eye;
            _camera.transform.rotation = Quaternion.LookRotation(face - eye, up);

            _gazeHeld = _worldRenderer.TryBeginPortraitGaze(_pendingNpcId, eye);
            _convergeFrames = _gazeHeld ? GazeConvergeFrames : 1;
            _phase = BakePhase.Converge;
        }

        // Снять взгляд и закрыть съёмку. Вызывается на КАЖДОМ выходе, в том
        // числе на отказном: иначе колонистка так и останется смотреть в точку,
        // где висела камера.
        private void FinishBake()
        {
            if (_gazeHeld && _worldRenderer != null && _pendingNpcId >= 0)
            {
                _worldRenderer.EndPortraitGaze(_pendingNpcId);
            }

            _gazeHeld = false;
            _pendingNpcId = -1;
            _phase = BakePhase.Idle;
            _nextBakeTime = Time.unscaledTime + BakeSpacingSeconds;
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
            RenderTexture.active = previous;

            // §90: круглая маска. Квадратный кадр с однотонной подложкой рядом
            // с круглыми аватарками отношений выглядит инородно, а над головой
            // он ещё и читается как «плашка», а не как лицо.
            //
            // Маска пишется В САМУ ТЕКСТУРУ, а не поверх шейдером: снимок и так
            // делается раз в игровые сутки, поэтому дешевле один раз обнулить
            // альфу по углам, чем гонять отдельный материал и в пузыре, и в
            // трёх местах панели.
            ApplyCircleMask(texture);
            texture.Apply(false);

            _bakedAtTick[npcId] = _lastSweepTick;
            _bakedDay[npcId] = _currentDay;

            // Спрайт держит ссылку на текстуру, а не копию, но пересоздать его
            // всё равно надо: Sprite кэширует размеры на момент создания.
            if (_sprites.TryGetValue(npcId, out var stale) && stale != null)
            {
                Destroy(stale);
                _sprites.Remove(npcId);
            }
        }

        // Мягкий край в один пиксель: жёсткая обрезка даёт «лесенку» по кругу,
        // особенно заметную на маленькой аватарке в списке отношений.
        private static void ApplyCircleMask(Texture2D texture)
        {
            var size = texture.width;
            var pixels = texture.GetPixels32();
            var centre = (size - 1) * 0.5f;
            var radius = centre;
            var inner = radius - 1.5f;

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var dx = x - centre;
                    var dy = y - centre;
                    var d = Mathf.Sqrt(dx * dx + dy * dy);
                    if (d <= inner)
                    {
                        continue;
                    }

                    var i = y * size + x;
                    var a = d >= radius ? 0f : 1f - (d - inner) / (radius - inner);
                    var p = pixels[i];
                    p.a = (byte)(p.a * a);
                    pixels[i] = p;
                }
            }

            texture.SetPixels32(pixels);
        }

        private void OnDestroy()
        {
            if (_gazeHeld && _worldRenderer != null && _pendingNpcId >= 0)
            {
                _worldRenderer.EndPortraitGaze(_pendingNpcId);
            }

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
