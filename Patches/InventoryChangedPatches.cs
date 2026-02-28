using HookDOTS.API.Attributes;
using Il2CppInterop.Runtime;
using KindredLogistics.Services;
using ProjectM;
using Unity.Collections;
using Unity.Entities;

namespace KindredLogistics.Patches;

public static class InventoryChangedPatches
{
    static EntityQuery _query;

    [EcsSystemUpdatePrefix(typeof(ReactToInventoryChangedSystem))]
    public static void ReactToInventoryChanged_Prefix()
    {
        if (!Core.HasInitialized) return;

        if (_query == default)
        {
            var eqb = new EntityQueryBuilder(Allocator.Temp)
                .AddAll(new(Il2CppType.Of<InventoryChangedEvent>(), ComponentType.AccessMode.ReadOnly));
            _query = Core.EntityManager.CreateEntityQuery(ref eqb);
            eqb.Dispose();
        }

        // Ensure reverse map is populated (one-shot on first call)
        ConveyorService.EnsureReverseMapPopulated();

        // Handle inventory change events
        if (!_query.IsEmptyIgnoreFilter)
        {
            var entities = _query.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (var entity in entities)
                {
                    var evt = entity.Read<InventoryChangedEvent>();
                    var invEntity = evt.InventoryEntity;
                    if (invEntity == Entity.Null || !Core.EntityManager.Exists(invEntity)) continue;

                    var territoryId = ConveyorService.LookupInventoryTerritory(invEntity);
                    if (territoryId >= 0)
                        ConveyorService.MarkTerritoryPending(territoryId);
                }
            }
            finally
            {
                entities.Dispose();
            }
        }
    }
}
