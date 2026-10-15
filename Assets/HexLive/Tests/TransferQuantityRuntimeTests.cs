#if UNITY_EDITOR
using System;
using System.Collections;
using System.Reflection;
using System.IO;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Runtime;
using HexLive.UnityPresentation.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace HexLive.Tests
{
    public sealed class TransferQuantityRuntimeTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static object Field(object owner, string name) => owner.GetType().GetField(name, Private).GetValue(owner);
        private static object Call(object owner, string name, params object[] args) => owner.GetType().GetMethod(name, Private).Invoke(owner, args);
        private static object Stack(int count)
        {
            var type = typeof(LootTransferPanel).GetNestedType("DragItem", BindingFlags.NonPublic);
            return Activator.CreateInstance(type, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new object[] { 1, InventoryItemSource.Carried, 0, count, ContentIds.Stick }, null);
        }

        [UnityTest]
        public IEnumerator NumberAndActualThumbAgreeAfterEditingAndReopening()
        {
            var host = new GameObject("Transfer quantity verification");
            var document = host.AddComponent<UIDocument>();
            var panel = host.AddComponent<LootTransferPanel>(); panel.enabled = false;
            var settings = document.panelSettings;
            var target = new RenderTexture(1920, 1080, 24); target.Create(); settings.targetTexture = target;
            try
            {
                document.rootVisualElement.style.display = DisplayStyle.Flex;
                ((RuntimeWindow)Field(panel, "_windowModal")).Show(true);
                Call(panel, "ShowQuantityPicker", -100, Stack(20));
                yield return null; yield return null;
                var slider = (SliderInt)Field(panel, "_quantitySlider");
                var input = (IntegerField)Field(panel, "_quantityInput");
                var thumb = slider.Q("unity-dragger");
                Assert.That(thumb, Is.Not.Null);
                var maximum = thumb.worldBound.center.x;
                Assert.That(slider.worldBound.width, Is.GreaterThan(100f), "Fixture must lay out the real visible control.");
                input.value = 1;
                yield return null;
                Assert.That(slider.value, Is.EqualTo(1));
                Assert.That(input.value, Is.EqualTo(1));
                Assert.That(thumb.worldBound.center.x, Is.LessThan(maximum - 50f),
                    "The actual visible thumb must leave the maximum when the number is one.");
                var old = RenderTexture.active;
                var bounds = document.rootVisualElement.Q("loot-quantity-card").worldBound;
                var image = new Texture2D(Mathf.CeilToInt(bounds.width), Mathf.CeilToInt(bounds.height), TextureFormat.RGBA32, false);
                try
                {
                    RenderTexture.active = target;
                    image.ReadPixels(new Rect(bounds.x, target.height-bounds.yMax, image.width, image.height), 0, 0); image.Apply();
                    Directory.CreateDirectory("Build/MSIRelease/20261014-transfer-quantity/visual");
                    File.WriteAllBytes("Build/MSIRelease/20261014-transfer-quantity/visual/quantity-one.png", image.EncodeToPNG());
                }
                finally { RenderTexture.active = old; Object.DestroyImmediate(image); }
                input.value = 20;
                yield return null;
                Assert.That(thumb.worldBound.center.x, Is.EqualTo(maximum).Within(1f));
                Call(panel, "HideQuantityPicker");
                yield return null;
                Call(panel, "ShowQuantityPicker", -100, Stack(8));
                yield return null; yield return null;
                Assert.That(slider.value, Is.EqualTo(8));
                Assert.That(input.value, Is.EqualTo(8));
                input.value = 1;
                yield return null;
                Assert.That(thumb.worldBound.center.x, Is.LessThan(maximum - 50f));
            }
            finally
            {
                Object.DestroyImmediate(host); Object.DestroyImmediate(settings);
                target.Release(); Object.DestroyImmediate(target);
            }
        }

        [UnityTest]
        public IEnumerator DropWindowUsesTheSameChoiceAndRejectsStaleInputEvents()
        {
            var host = new GameObject("Drop quantity shared choice verification");
            var document = host.AddComponent<UIDocument>();
            var panel = host.AddComponent<CharacterPanel>(); panel.enabled = false;
            var settings = document.panelSettings;
            try
            {
                var choice = (InventoryQuantityChoice)Field(panel, "_inventoryDropQuantityChoice");
                choice.Configure(20);
                using (var change = ChangeEvent<int>.GetPooled(0, 1))
                { change.target = choice.Input; choice.Input.SendEvent(change); }
                Assert.That(choice.Value, Is.EqualTo(20));
                Assert.That(choice.Input.value, Is.EqualTo(20));
                choice.Input.value = 1;
                yield return null;
                Assert.That(choice.Value, Is.EqualTo(1));
                Assert.That(choice.Slider.value, Is.EqualTo(1));
                choice.Configure(8);
                Assert.That(choice.Value, Is.EqualTo(8));
                choice.Slider.value = 3;
                yield return null;
                Assert.That(choice.Input.value, Is.EqualTo(3));
                Assert.That(choice.Value, Is.EqualTo(3));
            }
            finally { Object.DestroyImmediate(host); Object.DestroyImmediate(settings); }
        }

        [UnityTest]
        public IEnumerator NumericEditsCannotLeaveDifferentQuantitiesOnTheTwoControls()
        {
            var host = new GameObject("Transfer quantity range verification");
            var document = host.AddComponent<UIDocument>();
            var panel = host.AddComponent<LootTransferPanel>(); panel.enabled = false;
            var settings = document.panelSettings;
            try
            {
                document.rootVisualElement.style.display = DisplayStyle.Flex;
                ((RuntimeWindow)Field(panel, "_windowModal")).Show(true);
                Call(panel, "ShowQuantityPicker", -100, Stack(20));
                yield return null;
                var slider = (SliderInt)Field(panel, "_quantitySlider");
                var input = (IntegerField)Field(panel, "_quantityInput");
                foreach (var number in new[] { 200, -3, 1, 19, 20 })
                {
                    input.value = number;
                    yield return null;
                    var expected = Mathf.Clamp(number, 1, 20);
                    Assert.That(input.value, Is.EqualTo(expected), "Visible number must be the accepted quantity.");
                    Assert.That(slider.value, Is.EqualTo(input.value));
                }
            }
            finally { Object.DestroyImmediate(host); Object.DestroyImmediate(settings); }
        }

        [UnityTest]
        public IEnumerator OldRangeChangeCannotOverwriteTheNewDragQuantity()
        {
            var host = new GameObject("Transfer queued change verification");
            var document = host.AddComponent<UIDocument>();
            var panel = host.AddComponent<LootTransferPanel>(); panel.enabled = false;
            var settings = document.panelSettings;
            try
            {
                document.rootVisualElement.style.display = DisplayStyle.Flex;
                ((RuntimeWindow)Field(panel, "_windowModal")).Show(true);
                Call(panel, "ShowQuantityPicker", -100, Stack(20));
                yield return null; yield return null;
                var slider = (SliderInt)Field(panel, "_quantitySlider");
                var input = (IntegerField)Field(panel, "_quantityInput");
                // Bounds changes can queue value events while an event is being
                // dispatched. Simulate the old range's pending "one" after the
                // new drag has already set both controls to the full stack.
                using (var change = ChangeEvent<int>.GetPooled(0, 1))
                { change.target = slider; slider.SendEvent(change); }
                Assert.That(slider.value, Is.EqualTo(20));
                Assert.That(input.value, Is.EqualTo(slider.value), "Old event must not leave a full slider beside number one.");
            }
            finally { Object.DestroyImmediate(host); Object.DestroyImmediate(settings); }
        }
    }
}
#endif
