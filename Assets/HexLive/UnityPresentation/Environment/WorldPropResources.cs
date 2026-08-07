#nullable enable
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// Single Player-safe resource boundary for authored world props. Runtime
    /// ids never point at glTF ScriptedImporter assets: every migrated model is
    /// loaded from a native FBX mirror whose mesh/material sub-assets Unity can
    /// retain in Player data.
    /// </summary>
    public static class WorldPropResources
    {
        public static string NativeName(string id) => id switch
        {
            "campfire.spot" => "campfire_final_native",
            "bed.leaf" => "bed_leaf_final_native",
            "bed.basic" => "bed_basic_final_native",
            "station.water_collector" => "water_collector_final_native",
            "tree.palm" => "palm_final_native",
            // Crown ids are intentionally absent: PalmCrownFactory assembles
            // them from the approved palm_frond_native leaf_final mirror.
            "resource.log" => "resource.log",
            "resource.stick" => "resource.stick",
            "resource.palm_leaf" => "palm_frond_native",
            "resource.hide" => "resource_hide_native",
            "food.meat_raw" => "food_meat_raw_native",
            "food.meat_cooked" => "food_meat_cooked_native",
            "tool.lighter" => "tool_lighter_native",
            "item.bandage" => "item_bandage_native",
            "tool.bottle" => "tool_bottle_native",
            "tool.machete" => "tool_machete_native",
            "tool.saw" => "tool_saw_native",
            "rock.boulder" => "rock_boulder_native",
            "resource.stone" => "stone_single_native",
            _ => id
        };

        public static GameObject? Load(string id) =>
            Resources.Load<GameObject>($"HexLive/Objects/{NativeName(id)}");
    }
}
