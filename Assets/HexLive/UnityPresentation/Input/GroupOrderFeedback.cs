#nullable enable
using HexLive.UnityPresentation.Localization;
using UnityEngine;

namespace HexLive.UnityPresentation.Input
{
    /// <summary>§123 aggregate simulation answer for the group command panel.</summary>
    public static class GroupOrderFeedback
    {
        private const float VisibleSeconds = 5f;
        private static float _stampedAt = float.NegativeInfinity;

        public static string Order { get; private set; } = string.Empty;
        public static int Selected { get; private set; }
        public static int Manual { get; private set; }
        public static int Accepted { get; private set; }
        public static int NoPath { get; private set; }
        public static int Incapacitated { get; private set; }
        public static int Ai { get; private set; }

        public static bool IsFresh => Order.Length > 0 &&
            Time.unscaledTime - _stampedAt < VisibleSeconds;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Order = string.Empty;
            Selected = Manual = Accepted = NoPath = Incapacitated = Ai = 0;
            _stampedAt = float.NegativeInfinity;
        }

        public static void Report(string message)
        {
            Order = ValueOf(message, "Order=");
            if (Order.Length == 0) return;
            Selected = NumberOf(message, "Selected=");
            Manual = NumberOf(message, "Manual=");
            Accepted = NumberOf(message, "Accepted=");
            NoPath = NumberOf(message, "NoPath=");
            Incapacitated = NumberOf(message, "Incapacitated=");
            Ai = NumberOf(message, "AI=");
            _stampedAt = Time.unscaledTime;
        }

        public static string LocalizedSummary()
        {
            if (Manual == 0 && Order != "SetManual")
                return Loc.Get("group.no_manual");

            var key = "group.result." + Order;
            return string.Format(Loc.Get(key), Accepted, Selected, NoPath, Incapacitated, Ai);
        }

        private static int NumberOf(string message, string key) =>
            int.TryParse(ValueOf(message, key), out var value) ? value : 0;

        private static string ValueOf(string message, string key)
        {
            if (string.IsNullOrEmpty(message)) return string.Empty;
            var start = message.IndexOf(key, System.StringComparison.Ordinal);
            if (start < 0) return string.Empty;
            start += key.Length;
            var end = message.IndexOf(' ', start);
            return end < 0 ? message.Substring(start) : message.Substring(start, end - start);
        }
    }
}
