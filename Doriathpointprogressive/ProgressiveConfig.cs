// ============================================================
//  Doriath (Point Progressive) — Config/ProgressiveConfig.cs
// ============================================================

namespace DoriathMod.Config
{
    public static class ProgressiveConfig
    {
        public const string ModName        = "Doriath (Point Progressive)";
        public const string ModDescription =
            "Heroes level up by earning progression points through their actions. " +
            "Each level unlocks new abilities. " +
            "Being revived by a teammate costs a level.";

        public static float DiscardXpMultiplier { get; } = 0.60f;
        public static int   MaxCardHandSize     { get; } = 14;
    }
}
