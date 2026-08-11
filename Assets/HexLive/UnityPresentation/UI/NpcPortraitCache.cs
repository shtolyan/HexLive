using System.Collections.Generic;
using HexLive.UnityPresentation.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

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

        // §80 r3: КАДР СНИМКА — свой, а не позаимствованный у карточки.
        //
        // Общий лицевой якорь (§107.4 r2) стоит на 2 см НИЖЕ линии глаз: так
        // скомпонована ШИРОКАЯ фотография в identity-card. В круглом снимке та
        // же точка читается как «голова уехала вверх» — макушка с волосами
        // упирается в край маски, а под подбородком остаётся пусто. Поэтому
        // снимок поднимает точку прицела над общим якорем и отходит чуть
        // дальше: центр круга приходится на середину головы ВМЕСТЕ с волосами,
        // и причёске есть куда не влезть.
        //
        // Обе величины — на модели ростом 1.7 м, домножаются на мировой
        // масштаб. Это ручки кадра: двигать их, а не якорь, иначе поедет и
        // карточка, которая скомпонована иначе.
        private const float FaceDistanceMeters = 0.86f;
        private const float HeadCentreLiftMeters = 0.05f;

        // Фон ПРОЗРАЧНЫЙ: снимок — вырезка персонажа, а не плашка. Тёмную
        // подложку под неё рисует та панель, которой она нужна.
        private static readonly Color Backdrop = new(0.10f, 0.12f, 0.14f, 0f);

        // Кадров на «посмотри в камеру», прежде чем нажать затвор: взгляд
        // ведёт Final-IK, и мгновенно он не доезжает.
        private const int GazeConvergeFrames = 4;

        // Между снимками — пауза в реальном времени: фотосессию на всю колонию
        // надо провести быстро, но не десятком ReadPixels в одном кадре.
        private const float BakeSpacingSeconds = 0.35f;

        // §80 r2: ВСПЫШКА. Портрет должен быть одинаково освещён у всех и в
        // любой час, иначе половина колонии на фото — тёмные силуэты. Свет
        // зажигается ТОЛЬКО на отрисовку портретной камеры (колбэки URP
        // begin/endCameraRendering) и гаснет сразу после, поэтому в игровом
        // кадре вспышки не видно: главная камера рисуется в другой момент.
        // Это ЗАПОЛНЯЮЩИЙ свет, а не студийная вспышка в упор: он складывается
        // с уже имеющимся освещением сцены, и первая версия (1.35) днём
        // выбивала лицо в белое. Задача — вытянуть ночь до читаемого, а не
        // пересветить день. r3: 0.42 всё ещё пересвечивал — убавлено.
        private const float FlashIntensity = 0.30f;
        private const float FlashRangeMeters = 2.5f;
        private static readonly Color FlashTint = new(1f, 0.97f, 0.92f);

        private RenderTexture _scratch;
        private Camera _camera;
        private Light _flash;
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
        /// Фотосессия. Первый заход — СРАЗУ, как только колония появилась в
        /// мире: лица нужны с первой секунды, а не «когда солнце дойдёт до
        /// десяти». Дальше — по разу в игровые сутки на каждую. Один NPC за
        /// проход: съёмка — это кадр камеры плюс чтение из GPU, и растянуть её
        /// по одному телу дешевле, чем снимать всех разом.
        ///
        /// Часа суток здесь больше нет: свет даёт ВСПЫШКА, поэтому ночной
        /// снимок не хуже полуденного — и, что важнее, они одинаковые.
        /// </summary>
        public void Sweep(int tick, int dayLengthTicks, IReadOnlyList<int> npcIds)
        {
            if (npcIds == null || npcIds.Count == 0)
            {
                return;
            }

            _lastSweepTick = tick;
            _currentDay = tick / Mathf.Max(1, dayLengthTicks);

            if (_pendingNpcId >= 0 || Time.unscaledTime < _nextBakeTime)
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

            // Вспышка висит на самой камере — свет всегда ровно оттуда, откуда
            // смотрит объектив, значит тени на лице одинаковы у всех. Теней не
            // бросает: они тут только добавили бы шума на маленьком кадре.
            var flashGo = new GameObject("PortraitFlash");
            flashGo.transform.SetParent(camGo.transform, false);
            _flash = flashGo.AddComponent<Light>();
            _flash.type = LightType.Point;
            _flash.color = FlashTint;
            _flash.intensity = FlashIntensity;
            _flash.range = FlashRangeMeters;
            _flash.shadows = LightShadows.None;
            _flash.enabled = false;

            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
        }

        // Вспышка горит РОВНО на отрисовку портретной камеры. В том же кадре
        // главная камера рисуется отдельным вызовом, и к её очереди свет уже
        // выключен — поэтому в игре вспышки не видно.
        private void OnBeginCameraRendering(ScriptableRenderContext ctx, Camera cam)
        {
            if (_flash != null && cam == _camera)
            {
                _flash.enabled = true;
            }
        }

        private void OnEndCameraRendering(ScriptableRenderContext ctx, Camera cam)
        {
            if (_flash != null && cam == _camera)
            {
                _flash.enabled = false;
            }
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

            // Затвор: камера уже отрисовала кого просили — забрать пиксели.
            if (_phase == BakePhase.Rendering)
            {
                Capture(_pendingNpcId);
                _camera.enabled = false;
                FinishBake();
                return;
            }

            if (_pendingNpcId < 0)
            {
                return;
            }

            if (_worldRenderer == null)
            {
                _worldRenderer = FindAnyObjectByType<HexWorldRenderer>();
                if (_worldRenderer == null)
                {
                    return;
                }
            }

            if (!_worldRenderer.TryGetNpcFace(
                    _pendingNpcId, out var face, out var forward, out var up, out var scale) ||
                !_worldRenderer.IsNpcPhotogenic(_pendingNpcId))
            {
                // Тела ещё нет в мире, или она лежит/плывёт — не портрет.
                // Отложим: снимок ничего не стоит, а кривой кадр останется на
                // сутки в каждой вкладке отношений.
                FinishBake();
                return;
            }

            // ⭐ Камера ПРИБИТА к кости головы и наводится ЗАНОВО каждый кадр,
            // включая тот, в котором щёлкает затвор. Раньше её наводили один
            // раз и замораживали на все кадры сходимости взгляда — а голова за
            // это время продолжала жить (шаг, дыхание, доворот), и лицо к
            // моменту съёмки уезжало из кадра. Гнаться сама за собой она больше
            // не может: на время съёмки вес головы у LookAtIK нулевой, кость
            // стоит как стояла, ведут только зрачки.
            AimAt(face, forward, up, scale, out var eye);

            switch (_phase)
            {
                case BakePhase.Converge:
                    if (--_convergeFrames > 0)
                    {
                        return;
                    }

                    _camera.enabled = true;
                    _phase = BakePhase.Rendering;
                    return;

                default:
                    _gazeHeld = _worldRenderer.TryBeginPortraitGaze(_pendingNpcId, eye);
                    _convergeFrames = _gazeHeld ? GazeConvergeFrames : 1;
                    _phase = BakePhase.Converge;
                    return;
            }
        }

        // Единственное место, где решается кадр: перед лицом, по осям лицевого
        // рига, на фиксированном расстоянии — одна и та же точка съёмки у всех.
        private void AimAt(Vector3 face, Vector3 forward, Vector3 up, float scale, out Vector3 eye)
        {
            // Прицел и объектив поднимаются ВМЕСТЕ. Поднять один объектив — это
            // наклонить камеру, то есть снимать сверху; ровно поэтому прежний
            // подъём объектива был занулён, а не увеличен.
            var aim = face + up * (HeadCentreLiftMeters * scale);
            eye = aim + forward * (FaceDistanceMeters * scale);

            // Горизонт держим по МИРУ, а не по темечку: наклон головы иначе
            // заваливает весь кадр, и в круглой аватарке это читается как брак
            // печати. Если её всё же перевернуло — падаем на ось головы.
            var levelUp = Vector3.Dot(up, Vector3.up) > 0.5f ? Vector3.up : up;

            _camera.transform.position = eye;
            _camera.transform.rotation = Quaternion.LookRotation(aim - eye, levelUp);
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

            // Одна строка на первый снимок каждой: если кадр снова окажется не
            // тем, разбор начнётся с чисел, а не с гипотезы.
            if (!_bakedAtTick.ContainsKey(npcId))
            {
                Debug.Log($"[NpcPortrait] baked npc={npcId} " +
                          $"dist={FaceDistanceMeters:0.###} lift={HeadCentreLiftMeters:0.###} " +
                          $"fov={_camera.fieldOfView:0.#} flash={FlashIntensity:0.##}");
            }

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
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;

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
