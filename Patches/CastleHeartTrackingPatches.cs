using HarmonyLib;
using ProjectM;
using ProjectM.Gameplay.Systems;
using Unity.Collections;

namespace KindredLogistics.Patches;

[HarmonyPatch(typeof(SpawnCastleTeamSystem), nameof(SpawnCastleTeamSystem.OnUpdate))]
internal class CastleHeartSpawnSystemPatch
{
    public static bool Prefix(SpawnCastleTeamSystem __instance)
    {
        var entities = __instance._MainQuery.ToEntityArray(Allocator.Temp);
        foreach (var castleHeartEntity in entities)
        {
            Core.TerritoryService.AddCastleHeart(castleHeartEntity);
        }
        entities.Dispose();
        return true;
    }
}

[HarmonyPatch(typeof(CastleHeartClearRaidStateSystem), nameof(CastleHeartClearRaidStateSystem.OnUpdate))]
internal class CastleHeartDestroySystemPatch
{
    public static bool Prefix(CastleHeartClearRaidStateSystem __instance)
    {
        var entities = __instance._DestroyedCastleHeartQuery.ToEntityArray(Allocator.Temp);
        foreach (var castleHeartEntity in entities)
        {
            Core.TerritoryService.RemoveCastleHeart(castleHeartEntity);
        }
        entities.Dispose();
        return true;
    }
}