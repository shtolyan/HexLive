using System;
using HexLive.UnityPresentation.Wearing;
using UnityEngine;
using UnityEngine.UIElements;
#if UNITY_EDITOR
using System.IO;
using System.Linq;
using HexLive.UnityPresentation.Wearing.Garments;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

namespace HexLive.UnityPresentation.WardrobeTest
{
public sealed partial class WardrobeTestBootstrap
{
    private static readonly ActorName[] ReviewActors = { ActorName.Marta, ActorName.Kshishtof };
    private bool _reviewMode;
    private Toggle _genitalVisibility;

#if UNITY_EDITOR
    private const string ReviewRoot = "Assets/HexLiveContent/People/Wardrobe/";
    private const string ReviewSelection = ReviewRoot + "release-selection.json";
    private const string ReviewSession = "HexLive.WardrobeTest.ReviewModern";

    [MenuItem("HexLive/Wardrobe/Review Modern Catalog")]
    public static void OpenModernReview()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode ||
            !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        EditorSceneManager.OpenScene("Assets/Scenes/WardrobeTest.unity");
        SessionState.SetBool(ReviewSession, true);
        EditorApplication.ExecuteMenuItem("Window/General/Game");
        EditorApplication.EnterPlaymode();
    }
#endif

    private bool TryCollectModernReview()
    {
#if UNITY_EDITOR
        if (!SessionState.GetBool(ReviewSession, false)) return false;
        SessionState.EraseBool(ReviewSession);
        _reviewMode = true;
        var catalog = JObject.Parse(File.ReadAllText(ReviewRoot + "release-catalog.json"));
        var preparation = JObject.Parse(File.ReadAllText(ReviewRoot + "preparation.json"));
        var exclusions = ReadReviewSelection()["excludedModels"].OfType<JObject>()
            .ToDictionary(entry => (string)entry["id"], entry => (bool?)entry["evidence"]?["playerReview"] == true,
                StringComparer.Ordinal);
        foreach (var model in catalog["records"].OfType<JObject>()
                     .Where(record => (string)record["type"] == "wear")
                     .GroupBy(record => (string)record["metadata"]["artId"], StringComparer.Ordinal))
        {
            bool excluded = exclusions.TryGetValue(model.Key, out bool playerReview);
            if (excluded && !playerReview) continue;

            string path = (string)model.First()["main"];
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var wear = prefab != null ? prefab.GetComponent<Wear>() : null;
            if (wear == null) throw new InvalidDataException($"Missing Modern fitting for {model.Key}: {path}");

            var variants = model.Select(record => AssetDatabase.LoadAssetAtPath<GarmentDefinition>((string)record["definition"]))
                .OrderBy(definition => definition != null && definition.id == model.Key ? 0 : 1).ToArray();
            if (variants.Any(definition => definition == null))
                throw new InvalidDataException($"Missing Modern variant for {model.Key}");

            var entry = new WearEntry
            {
                Key = path,
                Group = model.Key,
                DisplayName = variants[0].displayName,
                Asset = wear,
                Variants = variants,
                VariantId = variants[0].id,
                Approved = !excluded,
                ReviewSource = (string)preparation[model.Key]?["source"],
            };
            _entries.Add(entry);
            _byKey.Add(entry.Key, entry);
        }

        _entries.Sort((left, right) => string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase));
        Debug.Log($"[Wardrobe] Modern review loaded {_entries.Count} models and {_entries.Sum(entry => entry.Variants.Count)} variants.");
        return true;
#else
        return false;
#endif
    }

    private GameObject LoadReviewActor(ActorName actor)
    {
#if UNITY_EDITOR
        if (_reviewMode)
            return AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/HexLiveContent/People/Prefabs/Actors/{actor}.prefab");
#endif
        return null;
    }

    private void EquipPreview(WearEntry entry)
    {
        if (_bodyBones == null) return;
        if (_reviewMode) _bodyBones.Equip(entry.EquipKey, entry.Asset, SelectedVariant(entry)?.variantMaterials);
        else _bodyBones.Equip(entry.EquipKey, entry.Asset);
    }

    private void AddReviewApproval(VisualElement tile, WearEntry entry)
    {
#if UNITY_EDITOR
        if (!_reviewMode) return;

        tile.style.height = 118f;
        var approve = new Toggle { text = "Approve", tooltip = "Снять галочку, чтобы исключить модель и все её расцветки" };
        approve.style.color = Text;
        approve.style.fontSize = 9;
        approve.SetValueWithoutNotify(entry.Approved);
        approve.RegisterCallback<MouseDownEvent>(evt => evt.StopPropagation());
        approve.RegisterValueChangedCallback(evt =>
        {
            try
            {
                SaveReviewApproval(entry.Group, evt.newValue);
                entry.Approved = evt.newValue;
                approve.tooltip = evt.newValue ? "Модель в отборе" : "Модель исключена из отбора";
            }
            catch (Exception error)
            {
                approve.SetValueWithoutNotify(entry.Approved);
                approve.tooltip = "Не удалось сохранить решение. Подробности в Console.";
                Debug.LogException(error);
            }
        });
        tile.Add(approve);
#endif
    }

#if UNITY_EDITOR
    private static JObject ReadReviewSelection() => JObject.Parse(File.ReadAllText(ReviewSelection));

    private static void SaveReviewApproval(string id, bool approved)
    {
        var selection = ReadReviewSelection();
        var exclusions = (JArray)selection["excludedModels"];
        var existing = exclusions.OfType<JObject>().SingleOrDefault(entry => (string)entry["id"] == id);
        if (existing != null && (bool?)existing["evidence"]?["playerReview"] != true)
            throw new InvalidOperationException($"{id} was excluded before the current review.");

        if (approved)
        {
            existing?.Remove();
        }
        else if (existing == null)
        {
            var preparation = JObject.Parse(File.ReadAllText(ReviewRoot + "preparation.json"));
            string source = (string)preparation[id]?["source"];
            if (string.IsNullOrEmpty(source)) throw new InvalidDataException($"Missing Modern source for {id}");
            exclusions.Add(new JObject
            {
                ["id"] = id,
                ["source"] = source,
                ["reason"] = "Игрок снял Approve в Wardrobe Test.",
                ["evidence"] = new JObject { ["playerReview"] = true },
            });
        }

        // Keep the previous selection intact if writing the new one fails.
        string temporary = ReviewSelection + ".tmp";
        File.WriteAllText(temporary, selection.ToString() + "\n");
        File.Replace(temporary, ReviewSelection, null);
    }
#endif

    private void BuildGenitalVisibilityToggle(VisualElement panel)
    {
#if UNITY_EDITOR
        _genitalVisibility = new Toggle("Скрывать гениталии");
        _genitalVisibility.style.color = Text;
        _genitalVisibility.RegisterValueChangedCallback(evt =>
        {
            if (_selectedKey == null || !_byKey.TryGetValue(_selectedKey, out var entry) ||
                _girl != ActorName.Kshishtof || !entry.Asset.CoversPelvis) return;

            if (_reviewMode)
            {
                var source = AssetDatabase.LoadAssetAtPath<GameObject>(entry.ReviewSource)?.GetComponent<Wear>();
                if (source == null)
                {
                    _genitalVisibility.SetValueWithoutNotify(entry.Asset.HidesGenitals);
                    Debug.LogError($"[Wardrobe] Missing source prefab for {entry.Group}");
                    return;
                }
                SaveGenitalVisibility(source, evt.newValue);
            }

            SaveGenitalVisibility(entry.Asset, evt.newValue);
            _bodyBones.TakeOff(entry.EquipKey);
            EquipPreview(entry);
            RelaxSkinCulling();
        });
        panel.Add(_genitalVisibility);
#endif
    }

#if UNITY_EDITOR
    private static void SaveGenitalVisibility(Wear wear, bool hidesGenitals)
    {
        wear.SetHidesGenitals(hidesGenitals);
        EditorUtility.SetDirty(wear);
        AssetDatabase.SaveAssetIfDirty(wear);
    }
#endif

    private void RefreshGenitalVisibilityToggle(WearEntry entry)
    {
        if (_genitalVisibility == null) return;
        bool available = _girl == ActorName.Kshishtof && entry != null && entry.Asset.CoversPelvis;
        _genitalVisibility.style.display = available ? DisplayStyle.Flex : DisplayStyle.None;
        _genitalVisibility.SetValueWithoutNotify(available && entry.Asset.HidesGenitals);
    }
}
}
