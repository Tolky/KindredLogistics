using HookDOTS.API.Attributes;
using ProjectM;
using ProjectM.Network;
using Unity.Collections;

namespace KindredLogistics.Patches;
public static class NameableInteractablePatches
{
    static NameableInteractableSystem _system;

    [EcsSystemUpdatePrefix(typeof(NameableInteractableSystem))]
    public static void NameableInteractableSystem_Update_Prefix()
    {
        if (!Core.HasInitialized) return;

        if (_system == null)
            _system = Core.Server.GetExistingSystemManaged<NameableInteractableSystem>();

        if (_system._RenameQuery.IsEmptyIgnoreFilter) return;

        // Identify which territories are affected by reading FromCharacter on each rename event
        var entities = _system._RenameQuery.ToEntityArray(Allocator.Temp);
        try
        {
            bool anyUnresolved = false;
            foreach (var entity in entities)
            {
                if (entity.Has<FromCharacter>())
                {
                    var character = entity.Read<FromCharacter>().Character;
                    var territoryId = Core.TerritoryService.GetTerritoryId(character);
                    if (territoryId >= 0)
                    {
                        Services.RefinementStationsService.InvalidateTerritory(territoryId);
                        Core.Stash.InvalidateTerritory(territoryId);
                        Services.ConveyorService.MarkTerritoryPending(territoryId);
                        continue;
                    }
                }
                anyUnresolved = true;
            }

            if (anyUnresolved)
            {
                Services.RefinementStationsService.InvalidateAllTerritories();
                Core.Stash.InvalidateAllTerritories();
                for (int t = Services.TerritoryService.MIN_TERRITORY_ID; t <= Services.TerritoryService.MAX_TERRITORY_ID; t++)
                    Services.ConveyorService.MarkTerritoryPending(t);
            }
        }
        finally
        {
            entities.Dispose();
        }

        // Name string caches are cheap — flush globally so cached names get re-read
        Core.RefinementStations.FlushNameCache();
        Core.Stash.FlushNameCache();
    }
}
