using System.IO;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Wire;

namespace HexLive.Simulation.Soak
{

/// <summary>
/// Отпечаток ВСЕГО состояния мира на тике — через тот же кодек, которым мир едет
/// по проводу.
/// <para>
/// Зачем, если есть трасса событий: трасса показывает только выбранные типы, и
/// правка может разъехаться там, куда выбранный набор не смотрит. Кодек уже
/// умеет превращать мир в канонические байты (список сущностей отсортирован по
/// id обоими концами — иначе перетасовка словаря ломала бы дельты), поэтому
/// полный дифф состояния достаётся даром, без единой строки новой сериализации.
/// </para>
/// </summary>
public static class StateHash
{
    private static WorldSnapshot _reuse;
    private static readonly MemoryStream Buffer = new MemoryStream();

    public static string Of(WorldState world)
    {
        _reuse = WorldSnapshotExporter.Export(world, _reuse);

        Buffer.SetLength(0);
        using (var writer = new BinaryWriter(Buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WorldSnapshotCodec.Write(_reuse, writer, includeDebugDetails: false);
        }

        return Fnv1A64(Buffer.GetBuffer(), (int)Buffer.Length).ToString("x16");
    }

    private static ulong Fnv1A64(byte[] bytes, int length)
    {
        var hash = 14695981039346656037UL;
        for (var i = 0; i < length; i++)
        {
            hash ^= bytes[i];
            hash *= 1099511628211UL;
        }

        return hash;
    }
}

}
