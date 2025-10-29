using HookDOTS.API.Attributes;
using Il2CppInterop.Runtime;
using ProjectM.CastleBuilding.Rebuilding;
using ProjectM.Network;
using Unity.Collections;
using Unity.Entities;

namespace KindredLogistics.Patches;
public static class CastleRebuildPatches
{
    static EntityQuery rebuildTransferEvent;

    [EcsSystemUpdatePrefix(typeof(CastleRebuildRegistryServerEventSystem))]
    public static void RebuildRegistryServerEventSystem_Update_Prefix()
    {
        if (!Core.HasInitialized) return;
        if (rebuildTransferEvent == default)
        {
            var eqb = new EntityQueryBuilder(Allocator.Temp)
                          .AddAll(new(Il2CppType.Of<CastleRebuildTransferEvent>(), ComponentType.AccessMode.ReadOnly));
            rebuildTransferEvent = Core.EntityManager.CreateEntityQuery(ref eqb);
            eqb.Dispose();
        }

        var entities = rebuildTransferEvent.ToEntityArray(Allocator.Temp);
        foreach (var entity in entities)
        {
            var rebuildEvent = entity.Read<CastleRebuildTransferEvent>();
            var zoneIndex = rebuildEvent.SourceTerritory.ZoneIndex;

            Core.TerritoryService.MarkTerritoryRebuilding(zoneIndex);
        }
    }
}
