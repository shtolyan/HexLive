#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.Overlays;
using UnityEngine;
using UnityEngine.UIElements;

// ---------------------------------------------------------------------------
//  Compile Control
//
//  A floating panel (Scene View overlay) + menu items to:
//    * toggle Unity's "Auto Refresh" (auto-compile on focus / on script edit)
//    * manually compile when you want to
//
//  Turning Auto Compile OFF stops the constant domain reloads that keep
//  dropping the MCP bridge. Edit scripts freely, then hit "Compile" once.
//
//  Overlay:  Scene View -> ... (overlays menu) -> "HexLive Compile"
//  Menu:     HexLive/Compile/Auto Compile        (checkbox)
//            HexLive/Compile/Compile Now   (Ctrl/Cmd+Shift+B)
// ---------------------------------------------------------------------------
internal static class CompileControl
{
    // Unity 2021.2+ reads "kAutoRefreshMode" (0 = off, 1 = on); the old
    // "kAutoRefresh" key is dead on Unity 6 — writing only it is why the
    // toggle used to do nothing. We write both, plus hold a runtime
    // DisallowAutoRefresh() lock as the enforcement that always works.
    const string AutoRefreshModeKey = "kAutoRefreshMode";
    const string LegacyAutoRefreshKey = "kAutoRefresh";

    // Survives domain reloads (not editor restarts): are WE holding a
    // DisallowAutoRefresh lock right now? Keeps the native counter balanced.
    const string DisallowHeldKey = "HexLive.CompileControl.DisallowHeld";

    public const string MenuAuto = "HexLive/Compile/Auto Compile";
    public const string MenuCompile = "HexLive/Compile/Compile Now %#b";

    public static bool AutoCompile
    {
        get => EditorPrefs.GetInt(AutoRefreshModeKey, EditorPrefs.GetInt(LegacyAutoRefreshKey, 1)) != 0;
        set
        {
            EditorPrefs.SetInt(AutoRefreshModeKey, value ? 1 : 0);
            EditorPrefs.SetInt(LegacyAutoRefreshKey, value ? 1 : 0);
            SyncDisallowLock();
        }
    }

    // Re-assert the lock after every domain reload (the pref is the source
    // of truth; SessionState keeps the native counter balanced).
    [InitializeOnLoadMethod]
    static void OnLoad() => SyncDisallowLock();

    static void SyncDisallowLock()
    {
        var held = SessionState.GetBool(DisallowHeldKey, false);
        var wantOff = !AutoCompile;

        if (wantOff && !held)
        {
            AssetDatabase.DisallowAutoRefresh();
            SessionState.SetBool(DisallowHeldKey, true);
        }
        else if (!wantOff && held)
        {
            try
            {
                AssetDatabase.AllowAutoRefresh();
            }
            catch
            {
                // Counter already zero after an editor restart — fine.
            }

            SessionState.SetBool(DisallowHeldKey, false);
        }
    }

    public static void CompileNow()
    {
        // Explicit refresh works even while auto refresh is disallowed.
        AssetDatabase.Refresh();
        CompilationPipeline.RequestScriptCompilation();
    }

    public static bool IsCompiling => EditorApplication.isCompiling || EditorApplication.isUpdating;

    // ---- Menu ----
    [MenuItem(MenuAuto)]
    static void ToggleAuto() => AutoCompile = !AutoCompile;

    [MenuItem(MenuAuto, true)]
    static bool ToggleAutoValidate() { Menu.SetChecked(MenuAuto, AutoCompile); return true; }

    [MenuItem(MenuCompile)]
    static void MenuCompileNow() => CompileNow();
}

[Overlay(typeof(SceneView), "hexlive-compile", "HexLive Compile", true)]
internal class CompileControlOverlay : Overlay
{
    Toggle _auto;
    Label _status;

    public override VisualElement CreatePanelContent()
    {
        var root = new VisualElement { style = { minWidth = 150, paddingTop = 4, paddingBottom = 4, paddingLeft = 4, paddingRight = 4 } };

        _auto = new Toggle("Auto Compile") { value = CompileControl.AutoCompile, tooltip = "Off = scripts don't auto-compile (bridge stays alive). Use Compile to build." };
        _auto.RegisterValueChangedCallback(e => CompileControl.AutoCompile = e.newValue);
        root.Add(_auto);

        var btn = new Button(() => { CompileControl.CompileNow(); RefreshStatus(); }) { text = "Compile" };
        btn.style.marginTop = 4;
        root.Add(btn);

        _status = new Label { style = { marginTop = 4, unityFontStyleAndWeight = FontStyle.Italic, opacity = 0.7f } };
        root.Add(_status);

        EditorApplication.update += RefreshStatus;
        RefreshStatus();
        return root;
    }

    public override void OnWillBeDestroyed() => EditorApplication.update -= RefreshStatus;

    void RefreshStatus()
    {
        if (_auto != null && _auto.value != CompileControl.AutoCompile)
            _auto.SetValueWithoutNotify(CompileControl.AutoCompile);
        if (_status != null)
            _status.text = CompileControl.IsCompiling ? "compiling…" : "idle";
    }
}
#endif
