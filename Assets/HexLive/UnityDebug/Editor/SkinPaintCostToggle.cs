using HexLive.UnityPresentation.Wearing;
using UnityEditor;
using UnityEngine;

namespace HexLive.UnityDebug.Editor
{
    /// <summary>
    /// Spec 40.8-J: toggles SkinTexturePainter's repaint cost log.
    ///
    /// Painting is main-thread-only in Unity (RenderTexture, Blit, DrawTexture
    /// and GenerateMips all are), so when it stutters the question is always
    /// the same: how many paint targets did the repaint touch, and how many did
    /// it have to CREATE. A 2048² target with mips is ~22 MB and allocating one
    /// is a driver stall — a few of those in a frame IS the freeze.
    ///
    /// Turn it on, stage a dog fight, read the console.
    /// Menu: <b>HexLive ▸ Skin Paint ▸ Log Repaint Cost</b>.
    /// </summary>
    public static class SkinPaintCostToggle
    {
        private const string MenuPath = "HexLive/Skin Paint/Log Repaint Cost";

        [MenuItem(MenuPath)]
        private static void Toggle()
        {
            SkinTexturePainter.LogRepaintCost = !SkinTexturePainter.LogRepaintCost;
            Debug.Log($"[SkinPaint] repaint cost log {(SkinTexturePainter.LogRepaintCost ? "ON" : "OFF")}");
        }

        [MenuItem(MenuPath, true)]
        private static bool ToggleValidate()
        {
            Menu.SetChecked(MenuPath, SkinTexturePainter.LogRepaintCost);
            return true;
        }
    }
}
