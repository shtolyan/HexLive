using UnityEditor;
using UnityEngine;
using HexLive.UnityPresentation.Config;

namespace HexLive.UnityDebug.Editor
{
    /// <summary>
    /// One-click builder for the FISTS gear asset (рукопашка): creates/updates
    /// <c>Resources/HexLive/Gear/fist.asset</c> — the GearConfig with the empty
    /// gearId ("" = кулаки) whose <see cref="GearConfig.strikes"/> rows wire
    /// the four unarmed strike clips (Punch A/B, Kick A/B — the molly boxing
    /// and MMA-kick takes in AnimLibrary) plus the looping Boxing Stance as
    /// armedIdle (руки в стойке между ударами). The sim rolls a random strike
    /// per exchange and the view plays exactly that clip; the per-strike замах /
    /// доигрыш / перезарядка sliders live on the asset (default 0.2 s each).
    ///
    /// Idempotent AND tuning-safe: re-running refreshes the clip references
    /// but keeps any timings you already dialed in on existing rows.
    /// Menu: <b>HexLive ▸ Build Unarmed Combat (fists)</b>.
    /// </summary>
    public static class BuildUnarmedCombat
    {
        const string AssetPath = "Assets/Resources/HexLive/Gear/fist.asset";
        const string AnimDir = "Assets/ImportedActors/AnimLibrary/";

        // The looping boxing guard (руки в стойке) — swapped in for Idle while
        // the NPC fights bare-handed (NpcActorView bare-stance path).
        const string StanceFile = "Boxing Stance";

        // File name (with import suffixes) per strike row, in the SAME order
        // the sim's StrikeVariants are indexed.
        static readonly string[] StrikeFiles =
        {
            "Punch A_once_to65",
            "Punch B_once_to52",
            "Kick A_once_to48",
            "Kick B_once_to45",
        };

        [MenuItem("HexLive/Build Unarmed Combat (fists)")]
        public static void Build()
        {
            var config = AssetDatabase.LoadAssetAtPath<GearConfig>(AssetPath);
            var fresh = config == null;
            if (fresh)
            {
                config = ScriptableObject.CreateInstance<GearConfig>();
                AssetDatabase.CreateAsset(config, AssetPath);
            }

            config.gearId = string.Empty;   // "" = кулаки (GearCatalog.Fist)
            config.prefab = null;           // ничего в руке
            config.prefabResourcePath = string.Empty;
            config.usableAsWeapon = true;   // fists ARE a weapon…
            config.meleePriority = 0;       // …but the no-weapon fallback one
            config.craftable = false;
            config.armedIdle = Clip(StanceFile);

            if (fresh)
            {
                // The flat combat sheet mirrors the sim's fist defaults; the
                // per-strike rows below override it per exchange anyway.
                config.damage = 0.15f;
                config.hitDelaySeconds = 1.1f;
                config.attackDurationSeconds = 1.5f;
                config.cooldownSeconds = 1.5f;
            }

            var rows = new GearConfig.StrikeAnim[StrikeFiles.Length];
            var missing = 0;
            for (var i = 0; i < StrikeFiles.Length; i++)
            {
                // Keep the timings the user already tuned on this row.
                rows[i] = config.strikes != null && i < config.strikes.Length && config.strikes[i] != null
                    ? config.strikes[i]
                    : new GearConfig.StrikeAnim();
                rows[i].clip = Clip(StrikeFiles[i]);
                if (rows[i].clip == null) missing++;
            }

            config.strikes = rows;

            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssets();
            Debug.Log($"[UnarmedCombat] {(fresh ? "Created" : "Updated")} {AssetPath}: " +
                $"{rows.Length} strikes, {missing} clip(s) missing." +
                (missing > 0 ? " Import the Punch/Kick FBX into AnimLibrary and re-run." : "") +
                " Не забудь: HexLive ▸ Export Sim Data (JSON).");
        }

        static AnimationClip Clip(string fileName)
        {
            var fbx = AnimDir + fileName + ".fbx";
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(fbx))
            {
                if (asset is AnimationClip clip && !clip.name.StartsWith("__preview"))
                {
                    return clip;
                }
            }

            Debug.LogWarning($"[UnarmedCombat] Clip not found: {fbx}");
            return null;
        }
    }
}
