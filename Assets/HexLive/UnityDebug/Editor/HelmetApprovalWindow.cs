#nullable enable
using System.IO;
using UnityEditor;
using UnityEngine;

namespace HexLive.UnityDebug.Editor
{

/// <summary>
/// Bug #329: окно апрува финального экспорта шлема. Показывает свежезапечённый
/// albedo из RuntimeSource и по кнопке игрока (и только по ней) пересобирает
/// префабы racing+retro. Немодальное — мост редактора не блокирует. Решение
/// пишется в маркер-файл, чтобы агент снаружи видел вердикт без опроса моста.
/// </summary>
public sealed class HelmetApprovalWindow : EditorWindow
{
    private const string AlbedoPath =
        "Assets/HexLiveContent/RuntimeSource/Helmets/helmet_racing_albedo.png";

    private const string RetroAlbedoPath =
        "Assets/HexLiveContent/RuntimeSource/Helmets/helmet_retro_albedo.png";

    private const string MarkerPath = "Temp/helmet_racing_approval.txt";

    private Texture2D? _albedo;
    private Texture2D? _retroAlbedo;

    [MenuItem("HexLive/Helmet Racing — Approve Export")]
    public static void Open()
    {
        var window = GetWindow<HelmetApprovalWindow>(false, "Racing: апрув экспорта");
        window.minSize = new Vector2(420f, 560f);
        window.Show();
    }

    private void OnEnable()
    {
        _albedo = AssetDatabase.LoadAssetAtPath<Texture2D>(AlbedoPath);
        _retroAlbedo = AssetDatabase.LoadAssetAtPath<Texture2D>(RetroAlbedoPath);
    }

    private void OnGUI()
    {
        GUILayout.Label("Новая ливрея racing (бейк 2048², авторские декали)",
            EditorStyles.boldLabel);
        GUILayout.Label(
            "Апрув пересоберёт ТОЛЬКО префабы racing и retro.\n" +
            "Офсеты (headwearFit) остальных шлемов не трогаются.",
            EditorStyles.wordWrappedLabel);

        // r5: racing едет сырыми материалами из Blender (атласа больше нет) —
        // превью здесь только у retro; racing смотри прямо на FBX в Project.
        EditorGUILayout.HelpBox(
            "racing: экспорт как есть — все материалы и родные UV из Blender, " +
            "текстуры в helmet_racing.fbm. retro: родная текстура автора.",
            MessageType.Info);
        var size = Mathf.Min(position.width - 20f, 320f);
        if (_retroAlbedo != null)
        {
            GUILayout.Label("retro (родная текстура)");
            var rect2 = GUILayoutUtility.GetRect(size, size);
            GUI.DrawTexture(rect2, _retroAlbedo, ScaleMode.ScaleToFit);
        }

        GUILayout.FlexibleSpace();
        var old = GUI.backgroundColor;
        GUI.backgroundColor = new Color(0.55f, 0.85f, 0.55f);
        if (GUILayout.Button("Апрув — пересобрать racing+retro", GUILayout.Height(36f)))
        {
            HelmetWearBuilder.BuildRacingRetro();
            File.WriteAllText(MarkerPath, "approved");
            Debug.Log("HelmetApproval: approved — префабы пересобраны");
            Close();
        }

        GUI.backgroundColor = new Color(0.9f, 0.6f, 0.55f);
        if (GUILayout.Button("Отмена — ничего не менять", GUILayout.Height(28f)))
        {
            File.WriteAllText(MarkerPath, "cancelled");
            Debug.Log("HelmetApproval: cancelled");
            Close();
        }

        GUI.backgroundColor = old;
    }
}

}
