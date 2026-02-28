using Stunlock.Core;

namespace KindredLogistics
{
    internal static class Const
    {
        public const string RECEIVER_REGEX = @"r(\d+)";
        public const string SENDER_REGEX = @"s(\d+)";

        public static readonly PrefabGUID Buff_InCombat_PvPVampire = new PrefabGUID(697095869);
    }
}
