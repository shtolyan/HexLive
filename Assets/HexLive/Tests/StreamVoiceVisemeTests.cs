using HexLive.UnityPresentation.Audio;
using NUnit.Framework;
namespace HexLive.Tests
{
    public sealed class StreamVoiceVisemeTests
    {
#if UNITY_EDITOR
        [TestCase("Marta")]
        [TestCase("Kshishtof")]
        public void ActualLowPolyBodyRetainsAUsableMouth(string name)
        {
            var assets = UnityEditor.AssetDatabase.LoadAllAssetsAtPath(
                "Assets/HexLiveContent/People/Source/Models/" + name + ".fbx");
            var found = false;
            foreach (var asset in assets)
                if (asset is UnityEngine.Mesh mesh && NpcVoiceLipSync.HasVisemes(mesh)) { found = true; break; }
            Assert.That(found, Is.True, name + " source FBX must retain the viseme blendshapes");
        }
#endif
        [Test] public void RussianCharacterTimingsCloseForSpacesAndAfterSpeech()
        {
            var v = new StreamVoiceVisemes();
            v.Append(new[] { "м", "а", " ", "у" }, new[] { 0f, 100f, 250f, 300f }, new[] { 100f, 150f, 50f, 100f });
            Assert.That(v.Sample(.05f), Is.EqualTo(5));
            Assert.That(v.Sample(.15f), Is.EqualTo(0));
            Assert.That(v.Sample(.26f), Is.EqualTo(-1));
            Assert.That(v.Sample(.32f), Is.EqualTo(2));
            Assert.That(v.Sample(.5f), Is.EqualTo(-1));
        }
        [Test] public void ChunksQueueWithoutOverlapAndInterruptionClearsThem()
        {
            var v = new StreamVoiceVisemes();
            v.Append(new[] { "а" }, new[] { 0f }, new[] { 100f });
            v.Append(new[] { "у" }, new[] { 0f }, new[] { 100f });
            Assert.That(v.Sample(.05f), Is.EqualTo(0));
            Assert.That(v.Sample(.15f), Is.EqualTo(2));
            v.Clear();
            Assert.That(v.Sample(.16f), Is.EqualTo(-1));
        }
        [Test] public void EnglishDigraphUsesTheExistingCalibration()
        {
            var v = new StreamVoiceVisemes();
            v.Append(new[] { "s", "h" }, new[] { 0f, 100f }, new[] { 100f, 100f });
            Assert.That(v.Sample(.15f), Is.EqualTo(8));
        }
    }
}
