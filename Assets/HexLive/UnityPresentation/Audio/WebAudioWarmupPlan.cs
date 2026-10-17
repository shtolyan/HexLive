using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HexLive.UnityPresentation.Audio
{
    /// <summary>§168.6: bounded opening vocabulary, not the whole voice corpus.</summary>
    internal static class WebAudioWarmupPlan
    {
        internal const int MaxVoices = 8;
        private static readonly string[] SpeechIds =
        {
            "happy_topic_smalltalk", "happy_agree", "sad_hunger", "sad_thirst",
            "sleepy_tired", "hurt_wound", "hurt_bitten", "work_chop",
            "work_haul", "work_build", "work_craft", "happy_done",
            "sad_cold", "sad_heat", "sleepy_bed", "sleepy_wake", "hurt_sick",
            "sad_dirty", "sad_lonely", "happy_relief", "sad_wet", "sleepy_dream",
        };

        internal static string[] SelectFiles(IEnumerable<string> localeFiles, string voice)
        {
            var files = new HashSet<string>(localeFiles, StringComparer.Ordinal);
            var result = new List<string>();
            var prefix = "voice_" + voice.Trim().ToLowerInvariant() + "_";
            foreach (var speech in SpeechIds)
            {
                var group = prefix + speech;
                foreach (var file in files.Where(p => IsAudio(p) && GroupId(p) == group)
                             .OrderBy(p => p, StringComparer.Ordinal).Take(3))
                {
                    result.Add(file);
                    var viseme = Path.ChangeExtension(file, ".vis");
                    if (files.Contains(viseme)) result.Add(viseme);
                }
            }
            return result.ToArray();
        }

        private static bool IsAudio(string path) => Path.GetExtension(path).ToLowerInvariant()
            is ".fsb" or ".wav" or ".ogg";

        private static string GroupId(string path)
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            var split = stem.LastIndexOf('_');
            return split >= 0 && int.TryParse(stem.Substring(split + 1), out _) ? stem.Substring(0, split) : stem;
        }
    }
}
