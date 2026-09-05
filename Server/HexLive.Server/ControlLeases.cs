using System;
using System.Collections.Generic;

namespace HexLive.Server
{

/// <summary>
/// §144.2/§121.9: ЕДИНЫЙ реестр «кто сейчас имеет право приказывать конкретной
/// колонистке» — для всех внешних контуров сразу. Owner — строка с
/// неймспейсом: <c>mcp:&lt;session&gt;</c> у MCP-агента, <c>ws:&lt;guid&gt;</c>
/// у игрока по сети; коллизия между контурами исключена синтаксически.
/// Локальный игрок в реестре не участвует — его мир принадлежит только ему.
/// <para>
/// Мир один, а владельцев может быть несколько, и они друг друга не видят. Без
/// лиза два контура отдают противоположные приказы одной девушке, каждый видит
/// «принято» — а снаружи это выглядит как «ИИ дурит». Поэтому владение явное,
/// ровно одно на колонистку, и всякая команда сверяется с ним ДО симуляции.
/// </para>
/// <para>
/// ⭐ Лиз протухает по РЕАЛЬНОМУ времени, а не по тикам, и меньше §121.7
/// намеренно: сначала владение возвращается в общий котёл, потом симуляция
/// забирает саму колонистку. Истёкшие лизы копятся в списке
/// (<see cref="CollectExpired"/>): фоновый свип сервера обязан вернуть каждую
/// брошенную колонистку под ИИ (<c>SetManualControlCommand(false)</c>) —
/// раньше истечение молча оставляло её в ручном режиме до таймаута §121.7.
/// </para>
/// </summary>
public sealed class ControlLeases
{
    /// <summary>См. соображение выше: меньше §121.7 (300 с) намеренно.</summary>
    public const int DefaultTimeoutSeconds = 120;

    private readonly object _gate = new();
    private readonly Dictionary<int, Lease> _byNpc = new();
    private readonly List<(int NpcId, string Owner)> _expired = new();
    private readonly Func<DateTimeOffset> _now;
    private readonly int _timeoutSeconds;

    public ControlLeases(int timeoutSeconds = DefaultTimeoutSeconds, Func<DateTimeOffset>? now = null)
    {
        _timeoutSeconds = Math.Max(5, timeoutSeconds);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public int TimeoutSeconds => _timeoutSeconds;

    private sealed class Lease
    {
        public string Owner = string.Empty;
        public string Id = string.Empty;
        public DateTimeOffset LastSeen;
        public int TimeoutSeconds;
    }

    /// <summary>
    /// Берёт владение. Повторный запрос тем же владельцем — продление, а не
    /// отказ: владелец, потерявший свой ответ в сети, обязан уметь повторить
    /// вызов без разбора, чем он кончился (идемпотентность важнее строгости).
    /// </summary>
    public bool TryAcquire(int npcId, string owner, out string leaseId, out string heldBy)
        => TryAcquire(npcId, owner, _timeoutSeconds, out leaseId, out heldBy);

    /// <summary>§159: one controller may request a shorter 15..120 second TTL.</summary>
    public bool TryAcquire(
        int npcId,
        string owner,
        int timeoutSeconds,
        out string leaseId,
        out string heldBy)
    {
        timeoutSeconds = Math.Clamp(timeoutSeconds, 15, 120);
        lock (_gate)
        {
            Expire();
            if (_byNpc.TryGetValue(npcId, out var existing))
            {
                if (!string.Equals(existing.Owner, owner, StringComparison.Ordinal))
                {
                    leaseId = string.Empty;
                    heldBy = existing.Owner;
                    return false;
                }

                existing.LastSeen = _now();
                existing.TimeoutSeconds = timeoutSeconds;
                leaseId = existing.Id;
                heldBy = owner;
                return true;
            }

            var lease = new Lease
            {
                Owner = owner,
                Id = Guid.NewGuid().ToString("N"),
                LastSeen = _now(),
                TimeoutSeconds = timeoutSeconds,
            };

            _byNpc[npcId] = lease;
            leaseId = lease.Id;
            heldBy = owner;
            return true;
        }
    }

    /// <summary>Команду пропускаем и тем же движением продлеваем лиз.</summary>
    public bool TryRenew(int npcId, string owner, out string heldBy)
    {
        lock (_gate)
        {
            Expire();
            if (!_byNpc.TryGetValue(npcId, out var lease))
            {
                heldBy = string.Empty;
                return false;
            }

            if (!string.Equals(lease.Owner, owner, StringComparison.Ordinal))
            {
                heldBy = lease.Owner;
                return false;
            }

            lease.LastSeen = _now();
            heldBy = owner;
            return true;
        }
    }

    public bool Release(int npcId, string owner)
    {
        lock (_gate)
        {
            if (_byNpc.TryGetValue(npcId, out var lease) &&
                string.Equals(lease.Owner, owner, StringComparison.Ordinal))
            {
                _byNpc.Remove(npcId);
                return true;
            }

            return false;
        }
    }

    /// <summary>Операторский рычаг админки: снять лиз, НЕ спрашивая владельца.
    /// Возвращает прежнего владельца — для журнала и ответа панели.</summary>
    public bool ForceRelease(int npcId, out string previousOwner)
    {
        lock (_gate)
        {
            if (_byNpc.TryGetValue(npcId, out var lease))
            {
                previousOwner = lease.Owner;
                _byNpc.Remove(npcId);
                return true;
            }

            previousOwner = string.Empty;
            return false;
        }
    }

    /// <summary>World-swap: новый мир — чистый реестр. Истёкшие тоже забываются:
    /// возвращать под ИИ больше некого, мира с теми id нет.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _byNpc.Clear();
            _expired.Clear();
        }
    }

    /// <summary>
    /// Забирает накопившиеся ИСТЁКШИЕ лизы (и очищает список). Вызывается
    /// фоновым свипом сервера: каждая пара — колонистка, которой владелец
    /// перестал писать, и которую надо вернуть под ИИ.
    /// </summary>
    public void CollectExpired(List<(int NpcId, string Owner)> into)
    {
        lock (_gate)
        {
            Expire();
            into.AddRange(_expired);
            _expired.Clear();
        }
    }

    /// <summary>Всё, чем владеет этот контур, — для отчёта и для отключения.</summary>
    public List<int> OwnedBy(string owner)
    {
        lock (_gate)
        {
            Expire();
            var owned = new List<int>();
            foreach (var pair in _byNpc)
            {
                if (string.Equals(pair.Value.Owner, owner, StringComparison.Ordinal))
                {
                    owned.Add(pair.Key);
                }
            }

            owned.Sort();
            return owned;
        }
    }

    public List<(int npcId, string owner, int idleSeconds)> Snapshot()
    {
        lock (_gate)
        {
            Expire();
            var now = _now();
            var rows = new List<(int, string, int)>();
            foreach (var pair in _byNpc)
            {
                rows.Add((pair.Key, pair.Value.Owner,
                    (int)(now - pair.Value.LastSeen).TotalSeconds));
            }

            rows.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            return rows;
        }
    }

    /// <summary>Кто владеет — или пусто. Для сообщений об отказе.</summary>
    public string HolderOf(int npcId)
    {
        lock (_gate)
        {
            Expire();
            return _byNpc.TryGetValue(npcId, out var lease) ? lease.Owner : string.Empty;
        }
    }

    private void Expire()
    {
        var now = _now();
        List<int>? dead = null;
        foreach (var pair in _byNpc)
        {
            if ((now - pair.Value.LastSeen).TotalSeconds >= pair.Value.TimeoutSeconds)
            {
                (dead ??= new List<int>()).Add(pair.Key);
            }
        }

        if (dead == null)
        {
            return;
        }

        foreach (var npcId in dead)
        {
            _expired.Add((npcId, _byNpc[npcId].Owner));
            _byNpc.Remove(npcId);
        }
    }
}

}
