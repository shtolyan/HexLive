using System;
using System.Collections.Generic;
using HexLive.Simulation.Common;

namespace HexLive.Server.Mcp
{

/// <summary>
/// §144.2: кто сейчас имеет право приказывать конкретной колонистке.
/// <para>
/// Мир один, а агентов может быть несколько, и они друг друга не видят. Без
/// лиза два агента отдают противоположные приказы одной девушке, каждый видит
/// «принято» и каждый считает, что она делает его дело; поймать такое по
/// поведению почти невозможно — оно выглядит как «ИИ дурит». Поэтому владение
/// явное, ровно одно на колонистку, и всякая команда сверяется с ним ДО того,
/// как дойдёт до симуляции.
/// </para>
/// <para>
/// ⭐ Лиз протухает по РЕАЛЬНОМУ времени, а не по тикам: агент отваливается в
/// реальном мире (упал процесс, порвалась сеть), и мир, стоящий на паузе, не
/// должен держать его владение вечно. Это независимый от §121.7 слой: там
/// симуляция решает, когда БРОШЕННУЮ колонистку вернуть ИИ, здесь — кому из
/// подключённых можно говорить. Порядок такой, что оба срабатывают: лиз
/// отпускается раньше, чем симуляция успевает соскучиться.
/// </para>
/// </summary>
public sealed class McpControlLeases
{
    /// <summary>
    /// Сколько лиз живёт без единой команды. Меньше §121.7
    /// (ManualIdleReleaseSeconds) намеренно: сначала владение возвращается в
    /// общий котёл, и только потом симуляция забирает саму колонистку — иначе
    /// её увёл бы ИИ у формально живого владельца.
    /// </summary>
    public const int DefaultTimeoutSeconds = 120;

    private readonly object _gate = new();
    private readonly Dictionary<int, Lease> _byNpc = new();
    private readonly Func<DateTimeOffset> _now;
    private readonly int _timeoutSeconds;

    public McpControlLeases(int timeoutSeconds = DefaultTimeoutSeconds, Func<DateTimeOffset>? now = null)
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
    }

    /// <summary>
    /// Берёт владение. Повторный запрос тем же владельцем — продление, а не
    /// отказ: агент, потерявший свой ответ в сети, обязан уметь повторить
    /// вызов без разбора, чем он кончился (идемпотентность важнее строгости).
    /// </summary>
    public bool TryAcquire(int npcId, string owner, out string leaseId, out string heldBy)
    {
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
                leaseId = existing.Id;
                heldBy = owner;
                return true;
            }

            var lease = new Lease
            {
                Owner = owner,
                Id = Guid.NewGuid().ToString("N"),
                LastSeen = _now(),
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

    /// <summary>Всё, чем владеет этот агент, — для отчёта и для отключения.</summary>
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
            if ((now - pair.Value.LastSeen).TotalSeconds >= _timeoutSeconds)
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
            _byNpc.Remove(npcId);
        }
    }

    public static EntityId Entity(int npcId) => new EntityId(npcId);
}

}
