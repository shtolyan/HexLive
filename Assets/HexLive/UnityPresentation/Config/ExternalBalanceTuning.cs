using System;
using System.IO;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Content;
using UnityEngine;

namespace HexLive.UnityPresentation.Config
{
    /// <summary>
    /// §132: optional no-rebuild balance layer for a built player.
    /// ScriptableObjects remain the complete, inspector-friendly defaults;
    /// The production overlay is the atomic <c>config/simdata</c> object (§152).
    /// A direct file remains only as an explicit developer override.
    /// </summary>
    public static class ExternalBalanceTuning
    {
        public const string FileName = "balance.json";
        public const string CommandLineArgument = "-hexlive-balance";

        public static void LoadAndApply()
        {
            var path = ResolvePath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return;
            }

            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[BalanceTuning] External override unreadable at {path}: {exception.Message}");
                return;
            }

            if (!SimDataFile.TryApplyBalanceOverrides(json, out var applied, out var error))
            {
                // The sim-side validator is atomic: the asset defaults loaded
                // immediately before this call remain intact.
                Debug.LogError($"[BalanceTuning] External override REJECTED at {path}: {error}");
                return;
            }

            Debug.Log($"[BalanceTuning] Applied {applied} external override(s) from {path}");
        }

        /// <summary>
        /// Loads the full local-simulation catalog from atomic config/simdata.
        /// Remote sessions intentionally do not call this: their server ships
        /// its own simdata in the wire handshake.
        /// </summary>
        public static void LoadAtomic(System.Action<bool> completed)
        {
            ContentAssetService.Instance.GetRawFile("config", "simdata", path =>
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    Debug.LogError("[SimData] No verified config/simdata object is available.");
                    completed?.Invoke(false);
                    return;
                }

                try
                {
                    var json = File.ReadAllText(path);
                    if (!SimDataFile.ApplyJson(json))
                    {
                        Debug.LogError("[SimData] Atomic config/simdata is invalid.");
                        completed?.Invoke(false);
                        return;
                    }

                    Debug.Log($"[SimData] Applied atomic config/simdata from verified blob {path}.");
                    completed?.Invoke(true);
                }
                catch (Exception exception)
                {
                    Debug.LogError($"[SimData] Atomic config/simdata is unreadable: {exception.Message}");
                    completed?.Invoke(false);
                }
            });
        }

        public static string ResolvePath()
        {
            string[] args;
            try
            {
                args = System.Environment.GetCommandLineArgs();
            }
            catch (Exception)
            {
                args = Array.Empty<string>();
            }

            for (var i = 0; i + 1 < args.Length; i++)
            {
                if (string.Equals(
                        args[i], CommandLineArgument, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    return Path.GetFullPath(args[i + 1]);
                }
            }

            // No implicit file beside the Player: that recreated a hidden
            // release/content-set coupling. Production config arrives through
            // ContentAssetService; only -hexlive-balance opts into a local file.
            return null;
        }
    }
}
