#nullable enable
using System.Collections.Generic;
using HexLive.UnityPresentation.UI;
using UnityEngine;

namespace HexLive.UnityPresentation.Audio
{
    /// <summary>
    /// Spec §70: музыка «по-майнкрафтовски». Не саундтрек, играющий поверх
    /// всего, а редкий гость: долгая тишина → трек тихо всплывает фейдом →
    /// играет целиком → уходит в тишину → снова долгая пауза. Остров при этом
    /// продолжает звучать сам (прибой/птицы/сверчки, §67.3) — музыка ложится
    /// поверх и намеренно тише эмбиента.
    ///
    /// Два режима, переключаются сами по <see cref="LoadingScreen.IsActive"/>:
    ///   • МЕНЮ — музыка почти сразу и с короткими паузами: экран статичный,
    ///     тишина там читается как «звук сломался»;
    ///   • ИГРА — первый трек через минуту-две, дальше 5–11 минут тишины.
    ///
    /// Треков может быть сколько угодно: live records `audio/*` с
    /// `metadata.kind=music`. Имена `menu_*` образуют плейлист меню,
    /// остальные — игровой; пустой плейлист падает на общий список, поэтому
    /// один-единственный трек обслуживает оба режима (§70.2).
    /// Часы у директора НЕсмасштабированные: меню Escape ставит Time.timeScale
    /// в 0, а музыка при этом обязана продолжать жить.
    /// </summary>
    public sealed class MusicDirector : MonoBehaviour
    {
        // ---- ручки (§70.3). Единственное место, где крутится музыка: в
        // Studio-проекте её нет, она идёт стримом мимо событий. ----
        private const float MenuVolume = 0.55f;
        private const float GameVolume = 0.40f;   // под музыкой живой эмбиент

        private const float MenuFirstDelay = 0.6f;
        private const float MenuGapMin = 6f;
        private const float MenuGapMax = 12f;
        private const float MenuFadeIn = 3.5f;

        // Первый трек в игре — не в упор к загрузке: сначала пусть будет слышно
        // остров. Дальше — редко, как в Minecraft (там 10–20 минут).
        private const float GameFirstSilenceMin = 75f;
        private const float GameFirstSilenceMax = 160f;
        private const float GameGapMin = 300f;
        private const float GameGapMax = 660f;
        private const float GameFadeIn = 8f;      // «всплывает из тишины»

        // Хвост гасим всегда: если трек и сам затухает — фейд не слышен, а если
        // обрывается резко, он спасает окончание.
        private const float TailFade = 3.5f;
        // Обрыв не по своей воле (меню → мир): уходим мягко.
        private const float InterruptFade = 4.5f;

        private bool _menuMode = true;
        private bool _started;
        private bool _playing;
        private bool _stopping;
        private float _fadeOutSeconds = InterruptFade;
        private float _fade;          // 0..1, множитель поверх громкости режима
        private float _nextTrackAt;
        private int _lengthMs = -1;   // снимается один раз на старте трека
        private string? _lastId;

        private void Awake()
        {
            var tracks = FmodSfx.MusicTracks;
            Debug.Log(tracks.Length > 0
                ? $"[Music] {tracks.Length} track(s): {string.Join(", ", tracks)}"
                : "[Music] no verified audio/music records yet — silence");
        }

        private void OnDestroy() => FmodSfx.StopMusic();

        private void Update()
        {
            // Режим определяем в ПЕРВОМ Update, а не в Awake: загрузочная
            // шторка создаётся тем же Boot(), и на момент нашего Awake её
            // IsActive может ещё не подняться — стартовали бы в игровом режиме
            // (первый трек через полторы минуты вместо «сразу в меню»).
            if (!_started)
            {
                _started = true;
                _menuMode = LoadingScreen.IsActive;
                _nextTrackAt = Time.unscaledTime + (_menuMode
                    ? MenuFirstDelay
                    : Random.Range(GameFirstSilenceMin, GameFirstSilenceMax));
            }

            var menu = LoadingScreen.IsActive;
            if (menu != _menuMode)
            {
                _menuMode = menu;
                // Мир проявился из-под загрузочной шторки — уводим меню-трек и
                // начинаем игровой отсчёт с чистой тишины.
                if (_playing)
                {
                    BeginFadeOut(InterruptFade);
                }
                else
                {
                    _nextTrackAt = Time.unscaledTime + (menu
                        ? Random.Range(MenuGapMin, MenuGapMax)
                        : Random.Range(GameFirstSilenceMin, GameFirstSilenceMax));
                }
            }

            if (_playing)
            {
                UpdatePlaying();
                return;
            }

            if (Time.unscaledTime >= _nextTrackAt)
            {
                StartTrack();
            }
        }

        private void UpdatePlaying()
        {
            // Трек доиграл сам — освобождаем стрим и уходим в тишину.
            if (!FmodSfx.IsMusicPlaying)
            {
                EndTrack();
                return;
            }

            // Последние секунды трека — тот же путь, что и обрыв: фейд вниз,
            // рассчитанный так, чтобы ноль пришёлся ровно на конец файла.
            if (!_stopping && _lengthMs > 0)
            {
                var position = FmodSfx.MusicPositionMs;
                if (position >= 0 && _lengthMs - position <= TailFade * 1000f)
                {
                    BeginFadeOut(TailFade);
                }
            }

            var fadeIn = _menuMode ? MenuFadeIn : GameFadeIn;
            _fade = _stopping
                ? Mathf.MoveTowards(_fade, 0f, Time.unscaledDeltaTime / _fadeOutSeconds)
                : Mathf.MoveTowards(_fade, 1f, Time.unscaledDeltaTime / fadeIn);

            FmodSfx.SetMusicVolume(_fade * (_menuMode ? MenuVolume : GameVolume));

            if (_stopping && _fade <= 0f)
            {
                EndTrack();
            }
        }

        private void StartTrack()
        {
            var id = PickTrack();
            if (id == null)
            {
                // Треков нет — не долбимся каждый кадр.
                _nextTrackAt = Time.unscaledTime + 60f;
                return;
            }

            if (!FmodSfx.PlayMusic(id, 0f))
            {
                _nextTrackAt = Time.unscaledTime + 30f;
                return;
            }

            _lastId = id;
            _playing = true;
            _stopping = false;
            _fade = 0f;
            _lengthMs = FmodSfx.MusicLengthMs; // один раз: у mp3 это скан файла
        }

        private void BeginFadeOut(float seconds)
        {
            _stopping = true;
            _fadeOutSeconds = Mathf.Max(0.1f, seconds);
        }

        private void EndTrack()
        {
            FmodSfx.StopMusic();
            _playing = false;
            _stopping = false;
            _fade = 0f;
            _lengthMs = -1;
            _nextTrackAt = Time.unscaledTime + (_menuMode
                ? Random.Range(MenuGapMin, MenuGapMax)
                : Random.Range(GameGapMin, GameGapMax));
        }

        // Плейлист режима, без повтора предыдущего трека (пока трек один —
        // повтор неизбежен и это нормально).
        private string? PickTrack()
        {
            var all = FmodSfx.MusicTracks;
            if (all.Length == 0)
            {
                return null;
            }

            var pool = new List<string>(all.Length);
            foreach (var id in all)
            {
                if (id.StartsWith("menu_", System.StringComparison.Ordinal) == _menuMode)
                {
                    pool.Add(id);
                }
            }

            if (pool.Count == 0)
            {
                pool.AddRange(all);
            }

            if (pool.Count > 1 && _lastId != null)
            {
                pool.Remove(_lastId);
            }

            return pool[Random.Range(0, pool.Count)];
        }
    }
}
