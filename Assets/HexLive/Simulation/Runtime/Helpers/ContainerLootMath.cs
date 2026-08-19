using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;

namespace HexLive.Simulation.Runtime
{

/// <summary>
/// §128.5: обыск ВЕЩИ, а не человека — истлевшего тела и лежащей вещи с
/// карманами.
/// <para>
/// Зачем: после §28.15C тело через двое суток становится одним лёгким
/// <c>remains.human</c>, и всё, что было при покойной, переезжает в его
/// <c>Contents</c>. Достать это было НЕЧЕМ: вещи умерших пропадали навсегда.
/// Та же дыра у снятого рюкзака — карманы уезжают в его Contents вместе с ним.
/// </para>
/// <para>
/// Модель нарочно простая и не повторяет человеческую раскладку (руки, слои,
/// кобура): у мешка нет тела, и городить ему анатомию значило бы врать. Ячейка
/// — это стопка одинаковых вещей, а <c>SourceIndex</c> — индекс её ПЕРВОГО
/// экземпляра в <c>Contents</c>, ровно как на человеческой стороне.
/// </para>
/// </summary>
internal static class ContainerLootMath
{
    /// <summary>Из этого можно доставать и в это можно класть.</summary>
    public static bool IsLootable(WorldState world, WorldObjectState obj)
    {
        if (obj is null)
        {
            return false;
        }

        if (obj.Contents.Count > 0)
        {
            return true;
        }

        // Пустой мешок — всё ещё мешок: в него кладут. А пустой камень — нет.
        // Мера — карманы вещи (InventoryCapacity) и явные теги: рюкзак с
        // нулевыми карманами контейнером не считается, и это правильно.
        return world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId, out var definition) &&
            (definition.HasTag("Remains") ||
             definition.HasTag("Container") ||
             definition.InventoryCapacity > 0);
    }

    /// <summary>
    /// Ячейки содержимого: стопка на каждый набор одинаковых id, в порядке
    /// первого появления. Порядок детерминирован — панель и приказ обязаны
    /// видеть одну и ту же нумерацию.
    /// </summary>
    public static void BuildCells(
        WorldObjectState obj, List<(string ItemId, int Count, int SourceIndex)> cells)
    {
        cells.Clear();
        for (var index = 0; index < obj.Contents.Count; index++)
        {
            var item = obj.Contents[index];
            var merged = false;
            if (InventoryState.IsStackable(item.DefinitionId))
            {
                for (var cell = 0; cell < cells.Count; cell++)
                {
                    if (cells[cell].ItemId != item.DefinitionId)
                    {
                        continue;
                    }

                    cells[cell] = (cells[cell].ItemId, cells[cell].Count + 1, cells[cell].SourceIndex);
                    merged = true;
                    break;
                }
            }

            if (!merged)
            {
                cells.Add((item.DefinitionId, 1, index));
            }
        }
    }

    /// <summary>
    /// Разрешить ячейку в конкретные экземпляры. Ожидаемый id проверяется: панель
    /// рисует прошлый тик, и к моменту приказа содержимое могло измениться.
    /// </summary>
    public static bool TryResolve(
        WorldObjectState obj, int slotIndex, string expectedDefinitionId, int requestedCount,
        out List<ItemInstance> items)
    {
        items = new List<ItemInstance>();
        var cells = new List<(string ItemId, int Count, int SourceIndex)>();
        BuildCells(obj, cells);
        if (slotIndex < 0 || slotIndex >= cells.Count)
        {
            return false;
        }

        var cell = cells[slotIndex];
        if (!string.IsNullOrEmpty(expectedDefinitionId) && cell.ItemId != expectedDefinitionId)
        {
            return false;
        }

        var take = requestedCount <= 0 ? 1 : System.Math.Min(requestedCount, cell.Count);
        foreach (var item in obj.Contents)
        {
            if (item.DefinitionId != cell.ItemId)
            {
                continue;
            }

            items.Add(item);
            if (items.Count == take)
            {
                break;
            }
        }

        return items.Count == take && items.Count > 0;
    }

    /// <summary>Влезет ли забранное в карманы обыскивающей.</summary>
    public static bool FitsInLooter(
        WorldState world, NPCState looter, IReadOnlyList<ItemInstance> moving)
    {
        var carried = new List<ItemInstance>(looter.Inventory.Items);
        carried.AddRange(moving);
        return PlayerInventoryMath.FitsProjected(
            world, looter, carried, new List<ItemInstance>(looter.WornItems));
    }

    public static void TakeFromContainer(
        WorldObjectState obj, NPCState looter, IReadOnlyList<ItemInstance> moving)
    {
        foreach (var item in moving)
        {
            obj.Contents.Remove(item);
            looter.Inventory.Items.Add(item);
        }
    }

    public static void GiveToContainer(
        WorldObjectState obj, NPCState looter, IReadOnlyList<ItemInstance> moving)
    {
        foreach (var item in moving)
        {
            looter.Inventory.Items.Remove(item);
            obj.Contents.Add(item);
        }
    }
}

}
