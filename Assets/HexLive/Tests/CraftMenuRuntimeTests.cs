#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HexLive.Simulation.AI;
using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.UI;
using HexLive.UnityPresentation.Wearing.Garments;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
    public sealed class CraftMenuRuntimeTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static object Field(object target, string name) => target.GetType().GetField(name, Private).GetValue(target);
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, Private).SetValue(target, value);
        private static object Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private).Invoke(target, args);
        private static void Click(Button button) => typeof(Clickable).GetMethod("SimulateSingleClick", Private | BindingFlags.Public)
            .Invoke(button.clickable, new object[] { null, 0 });

        [UnityTest]
        public IEnumerator CategoriesScrollAndBothDismissActionsCloseTheActualWindow()
        {
            var host = new GameObject("Craft menu verification");
            var document = host.AddComponent<UIDocument>(); var panel = host.AddComponent<CharacterPanel>();
            panel.enabled = false;
            var settings = document.panelSettings;
            settings.clearColor = true; settings.colorClearValue = Color.black;
            settings.clearDepthStencil = true;
            // UITK rounded/overflow clips need a stencil attachment.
            var target = new RenderTexture(1920, 1080, 24); target.Create(); settings.targetTexture = target;
            var cache = (Dictionary<string, Sprite>)typeof(ItemIcons).GetField("Cache", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var saved = new Dictionary<string, Sprite>(cache);
            try
            {
                var options = (List<CraftRecipeOption>)Field(panel, "_craftOptions");
                var rows = (JArray)JObject.Parse(File.ReadAllText("Assets/HexLiveContent/People/catalog.json"))["records"];
                foreach (var row in rows.Where(row => (string)row["type"] == "wear"))
                {
                    var id = (string)row["id"]; cache[id] = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/HexLiveContent/Icons/" + id + ".png");
                    var option = new CraftRecipeOption { Goal = GoalType.CraftItem, OutputDefinitionId = id, StationTag = "Workbench", CanCraft = true };
                    foreach (var input in row["metadata"]["simulation"]["craft"]["inputs"])
                        option.Ingredients.Add(new CraftIngredientOption { DefinitionId = (string)input["id"], Required = (int)input["count"], Available = 8 });
                    options.Add(option);
                }
                foreach (var id in new[] { "prosthetic.arm.wood", "resource.cloth", "tool.bow", "resource.hide", "resource.rope" })
                {
                    var iconId = id == "prosthetic.arm.wood" ? "prosthetic_arm_wood_l" : id;
                    cache[id] = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/HexLiveContent/Icons/" + iconId + ".png");
                }
                options.Add(new CraftRecipeOption { Goal = GoalType.CraftItem, OutputDefinitionId = "prosthetic.arm.wood", CanCraft = false, BlockReason = CraftBlockReason.MissingResources });
                options.Add(new CraftRecipeOption { Goal = GoalType.CraftItem, OutputDefinitionId = "resource.cloth" });
                options.Add(new CraftRecipeOption { Goal = GoalType.CraftItem, OutputDefinitionId = "tool.bow" });
                Set(panel, "_craftAvailable", true); Invoke(panel, "ToggleInventory"); Invoke(panel, "SelectInventoryPage", true, false);
                ((VisualElement)Field(panel, "_inventoryCraftTab")).style.display = DisplayStyle.Flex;
                var root = document.rootVisualElement; var grid = root.Q("craft-recipe-grid");
                Assert.That(grid.childCount, Is.EqualTo(85));
                Click(root.Q<Button>("craft-category-Clothing"));
                Assert.That(grid.childCount, Is.EqualTo(82));
                Assert.That(grid.Children().All(card => card.Q<Image>()?.sprite != null), Is.True);
                yield return null; yield return null; yield return null;
                var scroll = root.Q<ScrollView>("craft-recipe-scroll");
                var window = (VisualElement)Field(panel, "_inventoryWindow");
                Assert.That(window.worldBound.xMax - root.Q<Button>("inventory-close").worldBound.xMax, Is.LessThan(30), "Close control stays at the right edge when capacity is hidden.");
                Assert.That(scroll.contentContainer.layout.height, Is.GreaterThan(scroll.contentViewport.layout.height));
                scroll.scrollOffset = new Vector2(0, 300); yield return null;
                Assert.That(scroll.scrollOffset.y, Is.GreaterThan(0));
                scroll.scrollOffset = Vector2.zero;
                yield return null; yield return null;
                var previous = RenderTexture.active; var image = new Texture2D(1920, 1080, TextureFormat.RGBA32, false);
                try
                {
                    RenderTexture.active = target; image.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0); image.Apply();
                    var folder = Path.GetFullPath("Build/MSIRelease/20261012-seams/craft-tests"); Directory.CreateDirectory(folder);
                    File.WriteAllBytes(Path.Combine(folder, "craft-menu.png"), image.EncodeToPNG());
                }
                finally { RenderTexture.active = previous; Object.DestroyImmediate(image); }
                Click(root.Q<Button>("craft-category-Prosthetics"));
                Assert.That(grid.childCount, Is.EqualTo(1));
                Assert.That(Field(panel, "_selectedCraftOutput"), Is.EqualTo("prosthetic.arm.wood"));
                Click(root.Q<Button>("craft-category-Food"));
                Assert.That(grid.childCount, Is.Zero);
                Assert.That(((VisualElement)Field(panel, "_craftDetail")).style.display.value, Is.EqualTo(DisplayStyle.None));
                Click(root.Q<Button>("inventory-close"));
                Assert.That(Field(panel, "_inventoryOpen"), Is.False);
                Assert.That(CharacterPanel.BlocksWorldPointer, Is.True);
                yield return null; Assert.That(CharacterPanel.BlocksWorldPointer, Is.False);
                Invoke(panel, "ToggleInventory");
                var backdrop = root.Q("inventory-backdrop");
                using (var press = PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, button = 0 })) backdrop.SendEvent(press);
                Assert.That(Field(panel, "_inventoryOpen"), Is.False);
                Assert.That(backdrop.style.display.value, Is.EqualTo(DisplayStyle.None));
                Assert.That(CharacterPanel.BlocksWorldPointer, Is.True);
            }
            finally
            {
                cache.Clear(); foreach (var pair in saved) cache.Add(pair.Key, pair.Value);
                Object.DestroyImmediate(host); Object.DestroyImmediate(settings); target.Release(); Object.DestroyImmediate(target);
            }
        }
    }
}
#endif
