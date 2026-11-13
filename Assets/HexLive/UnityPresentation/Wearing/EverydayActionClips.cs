using HexLive.Simulation.Content;
using HexLive.UnityPresentation.Content;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{
    public sealed class EverydayActionClips : ScriptableObject
    {
        public const string ContentPath = "HexLive/EverydayActions";
#if UNITY_EDITOR
        public const string AssetPath = "Assets/HexLiveContent/RuntimeSource/EverydayActions.asset";
#endif

        public AnimationClip bedSleep;
        public AnimationClip bedEnter;
        public AnimationClip bedExit;
        public AnimationClip eatMeat;
        public AnimationClip eatMeatSeated;
        public AnimationClip eatCoconut;
        public AnimationClip eatCoconutSeated;
        public AnimationClip drinkBottle;
        public AnimationClip drinkBottleSeated;
        public AnimationClip drinkCup;
        public AnimationClip drinkCupSeated;
        public AnimationClip serveKneel;
        public AnimationClip serveWork;
        public AnimationClip serveRise;
        public AnimationClip serveStanding;
        public AnimationClip feedStanding;
        public AnimationClip receiveStanding;
        public AnimationClip climbFromWater;
        public AnimationClip giveDrink;
        public AnimationClip receiveDrink;
        public AnimationClip giveGift;
        public AnimationClip receiveGift;

        public bool HasAllClips => IsHumanoid(bedSleep) && bedSleep.isLooping &&
            IsHumanoid(bedEnter) && IsHumanoid(bedExit) &&
            IsHumanoid(eatMeat) && IsHumanoid(eatCoconut) && IsHumanoid(eatMeatSeated) &&
            IsHumanoid(eatCoconutSeated) && IsHumanoid(drinkBottle) && IsHumanoid(drinkBottleSeated) &&
            IsHumanoid(drinkCup) && IsHumanoid(drinkCupSeated) &&
            IsHumanoid(serveKneel) && IsHumanoid(serveWork) && IsHumanoid(serveRise) &&
            IsHumanoid(serveStanding) && IsHumanoid(feedStanding) && IsHumanoid(receiveStanding) &&
            IsHumanoid(climbFromWater) && IsHumanoid(giveDrink) && IsHumanoid(receiveDrink) &&
            IsHumanoid(giveGift) && IsHumanoid(receiveGift);

        private static bool IsHumanoid(AnimationClip clip) =>
            clip != null && clip.humanMotion && clip.length > 0f;

        internal AnimationClip Eating(string itemId, bool seated) => itemId switch
        {
            ContentIds.MeatCooked => seated ? eatMeatSeated : eatMeat,
            ContentIds.CoconutOpen => seated ? eatCoconutSeated : eatCoconut,
            _ => null
        };

        internal AnimationClip Drinking(string itemId, bool seated) => itemId switch
        {
            ContentIds.Bottle => seated ? drinkBottleSeated : drinkBottle,
            ContentIds.LeatherFlask or ContentIds.CoconutPierced or ContentIds.DrinkingGlass =>
                seated ? drinkCupSeated : drinkCup,
            _ => null
        };

        internal static EverydayActionClips Load()
        {
#if UNITY_EDITOR
            var local = UnityEditor.AssetDatabase.LoadAssetAtPath<EverydayActionClips>(AssetPath);
            if (local != null) return local;
#endif
            return AtomicResources.Load<EverydayActionClips>(ContentPath);
        }
    }
}
