using System.IO;
using UnityEditor;
using UnityEngine;

public sealed class HexLiveNewSeedFileWindow : EditorWindow
{
    private const string SeedPrefsKey = "HexLive.NewSeedFile.Seed";
    private const string FileNamePrefsKey = "HexLive.NewSeedFile.FileName";
    private const string DefaultSeed = "1104049673";
    private const string DefaultFileName = "hexlive_new_seed.txt";

    private string _seed;
    private string _fileName;
    private string _status;

    [MenuItem("HexLive/Save/New Seed File")]
    private static void Open()
    {
        var window = GetWindow<HexLiveNewSeedFileWindow>("HexLive Seed");
        window.minSize = new Vector2(420f, 155f);
        window.Show();
    }

    private void OnEnable()
    {
        _seed = EditorPrefs.GetString(SeedPrefsKey, DefaultSeed);
        _fileName = EditorPrefs.GetString(FileNamePrefsKey, DefaultFileName);
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("New-game seed file", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Creates a one-shot seed override in Application.persistentDataPath. " +
            "The loading screen consumes it on New game.",
            MessageType.Info);

        EditorGUI.BeginChangeCheck();
        _seed = EditorGUILayout.TextField("Seed / file contents", _seed);
        _fileName = EditorGUILayout.TextField("File name", _fileName);
        if (EditorGUI.EndChangeCheck())
        {
            EditorPrefs.SetString(SeedPrefsKey, _seed);
            EditorPrefs.SetString(FileNamePrefsKey, _fileName);
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Create file", GUILayout.Height(28f)))
            {
                CreateFile();
            }

            if (GUILayout.Button("Use current seed", GUILayout.Height(28f)))
            {
                _seed = DefaultSeed;
                EditorPrefs.SetString(SeedPrefsKey, _seed);
                CreateFile();
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.SelectableLabel(TargetPath, GUILayout.Height(18f));
            if (GUILayout.Button("Reveal", GUILayout.Width(70f)))
            {
                EditorUtility.RevealInFinder(TargetPath);
            }
        }

        if (!string.IsNullOrEmpty(_status))
        {
            EditorGUILayout.HelpBox(_status, MessageType.None);
        }
    }

    private string TargetPath =>
        Path.Combine(Application.persistentDataPath,
            string.IsNullOrWhiteSpace(_fileName) ? DefaultFileName : _fileName.Trim());

    private void CreateFile()
    {
        var text = (_seed ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            _status = "Nothing written: seed/content field is empty.";
            return;
        }

        try
        {
            Directory.CreateDirectory(Application.persistentDataPath);
            File.WriteAllText(TargetPath, text + "\n");
            _status = $"Created: {TargetPath}";
            AssetDatabase.Refresh();
        }
        catch (System.Exception e)
        {
            _status = $"Create failed: {e.Message}";
        }
    }
}
