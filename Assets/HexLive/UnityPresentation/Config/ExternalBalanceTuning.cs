using System;
using System.IO;
using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Wearing.Garments;
using UnityEngine;

namespace HexLive.UnityPresentation.Config
{
    /// <summary>
    /// §132: optional no-rebuild balance layer for a built player.
    /// ScriptableObjects remain the complete, inspector-friendly defaults;
    /// <c>HexLiveContent/balance.json</c> is a deliberately small deployment
    /// overlay applied after them and before world creation.
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

            // ExternalContentPath already resolves the shared directory beside
            // the .app/.exe correctly on macOS, Windows and Linux. Balance and
            // Addressables therefore deploy through one stable content root.
            return Path.Combine(ExternalContentPath.Root, FileName);
        }
    }
}
