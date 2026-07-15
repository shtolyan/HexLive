using System;
using HexLive.Simulation.Common;

namespace HexLive.UnityPresentation.Input
{
    /// <summary>
    /// Shared state for the currently inspected map hex. World input writes it;
    /// the top-right inspector reads it and rehydrates details from the latest
    /// simulation snapshot.
    /// </summary>
    public static class HexSelection
    {
        public static event Action<TileCoord?>? SelectionChanged;

        private static TileCoord? _selectedCoord;

        [UnityEngine.RuntimeInitializeOnLoadMethod(
            UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _selectedCoord = null;
            SelectionChanged = null;
        }

        public static bool HasSelection => _selectedCoord.HasValue;

        public static TileCoord SelectedCoord => _selectedCoord ?? TileCoord.Zero;

        public static void Select(TileCoord coord)
        {
            if (_selectedCoord.HasValue && _selectedCoord.Value == coord)
            {
                return;
            }

            _selectedCoord = coord;
            SelectionChanged?.Invoke(_selectedCoord);
        }

        public static void Clear()
        {
            if (!_selectedCoord.HasValue)
            {
                return;
            }

            _selectedCoord = null;
            SelectionChanged?.Invoke(null);
        }
    }
}
