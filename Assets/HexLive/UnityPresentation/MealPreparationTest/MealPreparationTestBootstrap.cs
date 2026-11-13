using UnityEngine;

namespace HexLive.UnityPresentation.MealPreparationTest
{
    [DisallowMultipleComponent]
    public sealed class MealPreparationTestBootstrap : MonoBehaviour
    {
        [Tooltip("Start with a single character and forageable materials instead of the stocked animation fixtures.")]
        [SerializeField] private bool _emptyHands;

        public bool EmptyHands => _emptyHands;
    }
}
