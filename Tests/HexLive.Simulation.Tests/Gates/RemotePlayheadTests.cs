using System;
using System.Collections.Generic;
using System.Linq;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Gates;

/// <summary>
/// §83.2.9: сетевые рывки — это часы, а не кодек. Здесь удалённый плейхед
/// прогоняется через синтетические расписания приходов (ровный поток, джиттер,
/// квантование старого poll-сервера, пачки, голодание, смена скорости) при
/// рендере 60 fps, и проверяется ОТОБРАЖАЕМАЯ скорость: NPC, идущая 1 юнит в
/// тик, обязана на экране идти ровно — что бы ни творила доставка. Старые часы
/// were untestable headless (жили в UnityPresentation), и их дефекты — оценка
/// темпа по кадрам рендера, всплеск двойной скорости при двух тиках за Update,
/// «замри-и-прыгни» при голодании — воспроизводятся тут как регрессы.
/// </summary>
public sealed class RemotePlayheadTests
{
    private const float TickDelta = 0.25f;
    private const double Fps = 60.0;
    private const double NominalTicksPerSecond = 1.0 / TickDelta;

    /// <summary>
    /// Рендер-цикл вокруг часов: приходы по расписанию, Advance раз в кадр,
    /// декод строго по TickToDecode — та же дисциплина, что в
    /// RemoteSocketBackend.Tick.
    /// </summary>
    private sealed class Harness
    {
        public readonly RemotePlayhead Clock = new();
        public double Now;
        public int Decoded = int.MinValue;
        public int Discontinuities;
        public readonly List<double> Displayed = new();
        public readonly List<float> Speed = new();

        private readonly Queue<(int Tick, double At)> _schedule = new();
        private readonly Queue<int> _inbox = new();

        public void Schedule(int tick, double at) => _schedule.Enqueue((tick, at));

        public void RunSeconds(double seconds)
        {
            var dt = 1.0 / Fps;
            var frames = (int)Math.Round(seconds * Fps);
            for (var i = 0; i < frames; i++)
            {
                Now += dt;
                while (_schedule.Count > 0 && _schedule.Peek().At <= Now)
                {
                    var (tick, at) = _schedule.Dequeue();
                    _inbox.Enqueue(tick);
                    Clock.OnFrameArrived(tick, at, out var discontinuity);
                    if (discontinuity)
                    {
                        Discontinuities++;
                        // Резинк = «другая сессия»: старая очередь недоставленных
                        // кадров бессмысленна, бэкенд в этот момент просит
                        // кейфрейм и чистит её.
                        _inbox.Clear();
                        _inbox.Enqueue(tick);
                    }
                }

                Clock.Advance(Now, (float)dt);

                while (_inbox.Count > 0 && _inbox.Peek() <= Clock.TickToDecode)
                {
                    Decoded = _inbox.Dequeue();
                    Clock.OnTickPresented(Decoded);
                }

                Displayed.Add(Clock.PresentedTick - 1 + Clock.TickAlpha);
                Speed.Add(Clock.SpeedMultiplier);
            }
        }

        /// <summary>Покадровые скорости (тики/с) начиная с warm-up секунды.</summary>
        public List<double> VelocitiesAfter(double warmupSeconds)
        {
            var start = Math.Max(1, (int)(warmupSeconds * Fps));
            var result = new List<double>();
            for (var i = start; i < Displayed.Count; i++)
            {
                result.Add((Displayed[i] - Displayed[i - 1]) * Fps);
            }

            return result;
        }
    }

    private static Harness Steady(int firstTick, double seconds, double transit = 0.03)
    {
        var h = new Harness();
        h.Clock.Configure(TickDelta, firstTick, declaredSpeed: 1f, paused: false);
        var ticks = (int)(seconds / TickDelta);
        for (var i = 0; i < ticks; i++)
        {
            h.Schedule(firstTick + i, i * TickDelta + transit);
        }

        return h;
    }

    [Test]
    public void SteadyStream_DisplayedVelocityLocksToNominal()
    {
        // Долгий прогон сознательно: после коннекта часы строят буфер лёгким
        // замедлением с постоянной времени ~8 с — это невидимо игроку и
        // проверяется отдельными полосами ±10%; здесь же меряется УСТАНОВИВШИЙСЯ
        // режим, и он обязан держать ±1%.
        var h = Steady(1000, seconds: 42);
        h.RunSeconds(40);

        var velocities = h.VelocitiesAfter(30);
        Assert.That(velocities, Is.Not.Empty);
        Assert.That(velocities.Min(), Is.GreaterThan(NominalTicksPerSecond * 0.99),
            "ровный поток, а показ замедляется — часы гоняются за шумом");
        Assert.That(velocities.Max(), Is.LessThan(NominalTicksPerSecond * 1.01),
            "ровный поток, а показ ускоряется — часы гоняются за шумом");

        for (var i = 1; i < h.Displayed.Count; i++)
        {
            Assert.That(h.Displayed[i], Is.GreaterThanOrEqualTo(h.Displayed[i - 1]),
                "плейхед пошёл назад — время на экране не может течь вспять");
        }
    }

    [Test]
    public void SpeedMultiplierIsThePlayheadsOwnDerivative()
    {
        // §83.2.9, главный закон: альфа и множитель скорости — из ОДНОЙ оценки.
        // Здесь он проверяется механически: заявленный множитель равен
        // фактической производной плейхеда, кадр за кадром.
        var h = Steady(1000, seconds: 12);
        h.RunSeconds(11);

        var start = (int)(2 * Fps);
        for (var i = start; i < h.Displayed.Count; i++)
        {
            var measured = (h.Displayed[i] - h.Displayed[i - 1]) * Fps * TickDelta;
            Assert.That(measured, Is.EqualTo(h.Speed[i]).Within(0.01),
                "SpeedMultiplier разошёлся с реальным движением альфы — ноги поедут");
        }
    }

    [Test]
    public void JitteredArrivals_NoVisibleSpeedBursts()
    {
        // ±80 мс равномерного джиттера — хуже реального Сингапура.
        var h = new Harness();
        h.Clock.Configure(TickDelta, 1000, 1f, paused: false);
        var rng = new Random(7);
        for (var i = 0; i < 48; i++)
        {
            h.Schedule(1000 + i, i * TickDelta + 0.03 + rng.NextDouble() * 0.08);
        }

        h.RunSeconds(11);

        var velocities = h.VelocitiesAfter(2);
        Assert.That(velocities.Min(), Is.GreaterThan(NominalTicksPerSecond * 0.9),
            "джиттер доставки пролез на экран замедлением");
        Assert.That(velocities.Max(), Is.LessThan(NominalTicksPerSecond * 1.1),
            "джиттер доставки пролез на экран ускорением");
    }

    [Test]
    public void PollQuantizedSends_TodaysServer_StaySmooth()
    {
        // Старый серверный цикл отправки замечает тик раз в 62.5 мс — кадры
        // уходят квантованными. Фаза 1 обязана чинить ЭТОТ паттерн одна, без
        // серверных правок: клиент против неизменённого продакшна.
        var h = new Harness();
        h.Clock.Configure(TickDelta, 1000, 1f, paused: false);
        for (var i = 0; i < 48; i++)
        {
            var ideal = i * TickDelta;
            var quantized = Math.Ceiling(ideal / 0.0625) * 0.0625;
            h.Schedule(1000 + i, quantized + 0.055);
        }

        h.RunSeconds(11);

        var velocities = h.VelocitiesAfter(2);
        Assert.That(velocities.Min(), Is.GreaterThan(NominalTicksPerSecond * 0.9));
        Assert.That(velocities.Max(), Is.LessThan(NominalTicksPerSecond * 1.1));
    }

    [Test]
    public void BurstDelivery_NeverShowsTwoTicksOfMotionInOneFrame()
    {
        // Прямой регресс-тест всплеска двойной скорости: три кадра приезжают
        // разом каждые 750 мс. Старые часы выдавали по два тика на Update, и
        // рендерер лерпил двухтиковую дистанцию за один тик альфы.
        var h = new Harness();
        h.Clock.Configure(TickDelta, 1000, 1f, paused: false);
        for (var group = 0; group < 16; group++)
        {
            var at = group * 0.75 + 0.03;
            for (var k = 0; k < 3; k++)
            {
                h.Schedule(1000 + group * 3 + k, at);
            }
        }

        h.RunSeconds(11);

        var perFrameMotionCap = NominalTicksPerSecond / Fps * 1.25;
        var start = (int)(3 * Fps);
        for (var i = start; i < h.Displayed.Count; i++)
        {
            Assert.That(h.Displayed[i] - h.Displayed[i - 1],
                Is.LessThanOrEqualTo(perFrameMotionCap),
                "пачка кадров показана рывком — всплеск скорости вернулся");
        }
    }

    [Test]
    public void Starvation_EasesToAStop_AndRecoveryIsRateCapped()
    {
        var h = new Harness();
        h.Clock.Configure(TickDelta, 1000, 1f, paused: false);
        // 5 секунд ровно, дыра в 1.25 с (5 тиков молчания), затем поток
        // продолжается по прежнему расписанию.
        for (var i = 0; i < 60; i++)
        {
            var at = i * TickDelta + 0.03;
            if (at is > 5.0 and <= 6.25)
            {
                at = 6.28; // опоздавшие кадры приезжают скопом после дыры
            }

            h.Schedule(1000 + i, at);
        }

        h.RunSeconds(14);

        var velocities = h.VelocitiesAfter(2);
        Assert.That(velocities.Min(), Is.GreaterThanOrEqualTo(-0.0001),
            "показ пошёл назад — snap-back, которого конструкция не допускает");
        Assert.That(velocities.Min(), Is.LessThan(0.5),
            "буфер кончился, а показ не остановился — экстраполяция без данных");
        Assert.That(velocities.Max(), Is.LessThan(NominalTicksPerSecond * 1.11),
            "догон после дыры превысил слив ±10% — это уже видимый рывок");
        Assert.That(h.Discontinuities, Is.Zero,
            "дыра в 5 тиков — это лаг, а не другая сессия; резинк тут запрещён");

        var lastSecond = velocities.Skip(velocities.Count - (int)Fps).ToList();
        Assert.That(lastSecond.Average(),
            Is.InRange(NominalTicksPerSecond * 0.95, NominalTicksPerSecond * 1.11),
            "после дыры темп не вернулся к номиналу");
    }

    [Test]
    public void Pause_DrainsTheBufferThenFreezes()
    {
        var h = Steady(1000, seconds: 4);
        h.RunSeconds(4);
        h.Clock.OnServerClock(1f, paused: true);
        h.RunSeconds(3);

        // §83: пауза показывает ТЕКУЩЕЕ состояние — буфер дорисован до
        // новейшего пришедшего тика, не заморожен на D тиков в прошлом.
        var newest = 1000 + (int)(4 / TickDelta) - 1;
        Assert.That(h.Decoded, Is.EqualTo(newest), "пауза не дорисовала буфер");
        Assert.That(h.Displayed[^1], Is.EqualTo(newest).Within(1e-6));
        Assert.That(h.Displayed[^1], Is.EqualTo(h.Displayed[^2]).Within(1e-9),
            "мир на паузе, а показ ещё движется");
    }

    [Test]
    public void ReconnectJump_ResyncsInsteadOfSlewingForMinutes()
    {
        var h = Steady(1000, seconds: 3);
        h.RunSeconds(3);

        var far = 1500;
        h.Schedule(far, h.Now + 0.05);
        h.RunSeconds(0.5);

        Assert.That(h.Discontinuities, Is.EqualTo(1),
            "скачок в сотни тиков обязан быть резинком, а не целью слива");
        Assert.That(h.Decoded, Is.EqualTo(far),
            "кейфрейм после резинка должен декодироваться сразу — иначе минутная заморозка");
    }

    [Test]
    public void FirstFrame_DecodesOnTheSameUpdate()
    {
        // Мир должен появиться на первом же кадре после первого прихода —
        // буфер интерполяции добирается потом, невидимым замедлением.
        var h = new Harness();
        h.Clock.Configure(TickDelta, 500, 1f, paused: false);
        h.Schedule(500, 0.005);
        h.RunSeconds(0.05);

        Assert.That(h.Decoded, Is.EqualTo(500));
    }

    [Test]
    public void AdaptiveDelay_GrowsFastAndShrinksSlow()
    {
        // Фаза 3 (включается после деплоя событийной отправки): задержка
        // интерполяции следует за измеренным джиттером — вверх сразу, вниз
        // не быстрее 0.05 тика в секунду.
        var h = new Harness();
        h.Clock.AdaptiveDelay = true;
        h.Clock.Configure(TickDelta, 1000, 1f, paused: false);
        var rng = new Random(11);
        for (var i = 0; i < 24; i++)
        {
            h.Schedule(1000 + i, i * TickDelta + 0.03 + rng.NextDouble() * 0.1);
        }

        h.RunSeconds(6);
        var afterJitter = h.Clock.DelayTicks;
        Assert.That(afterJitter, Is.GreaterThan(1.25f),
            "джиттер 100 мс, а буфер остался минимальным");

        for (var i = 24; i < 48; i++)
        {
            h.Schedule(1000 + i, i * TickDelta + 0.03);
        }

        h.RunSeconds(2);
        Assert.That(h.Clock.DelayTicks,
            Is.GreaterThanOrEqualTo(afterJitter - 0.11f),
            "буфер схлопнулся быстрее 0.05 тика/с — осцилляция задержки");
    }
}
