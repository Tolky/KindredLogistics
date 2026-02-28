using HookDOTS.API.Attributes;
using Il2CppInterop.Runtime;
using KindredLogistics.Services;
using ProjectM;
using ProjectM.Network;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace KindredLogistics.Patches;

public static class TerritoryPresencePatches
{
    // Player territory tracking: userEntity → last known territoryId
    static readonly Dictionary<Entity, int> _playerTerritories = new();
    static EntityQuery _userQuery;

    [EcsSystemUpdatePostfix(typeof(SetCurrentMapZoneSystem_Server))]
    public static void SetCurrentMapZone_Postfix()
    {
        if (!Core.HasInitialized) return;
        if (!Core.PlayerSettings.IsSolarEnabled(0)) return;

        if (_userQuery == default)
        {
            var eqb = new EntityQueryBuilder(Allocator.Temp)
                .AddAll(new(Il2CppType.Of<User>(), ComponentType.AccessMode.ReadOnly));
            _userQuery = Core.EntityManager.CreateEntityQuery(ref eqb);
            eqb.Dispose();
            Core.Log.LogInfo("[TerritoryPresence] HookDOTS postfix registered on SetCurrentMapZoneSystem_Server");
        }

        var entities = _userQuery.ToEntityArray(Allocator.Temp);
        try
        {
            foreach (var userEntity in entities)
            {
                var user = userEntity.Read<User>();
                if (!user.IsConnected)
                {
                    if (_playerTerritories.TryGetValue(userEntity, out var oldTerritory))
                    {
                        _playerTerritories.Remove(userEntity);
                        if (oldTerritory >= 0)
                            ConveyorService.MarkTerritoryPending(oldTerritory);
                    }
                    continue;
                }

                var character = user.LocalCharacter.GetEntityOnServer();
                if (character == Entity.Null) continue;

                var territoryId = Core.TerritoryService.GetTerritoryId(character);

                if (_playerTerritories.TryGetValue(userEntity, out var prevTerritory))
                {
                    if (prevTerritory != territoryId)
                    {
                        if (prevTerritory >= 0)
                            ConveyorService.MarkTerritoryPending(prevTerritory);
                        if (territoryId >= 0)
                            ConveyorService.MarkTerritoryPending(territoryId);
                        _playerTerritories[userEntity] = territoryId;
                    }
                }
                else
                {
                    _playerTerritories[userEntity] = territoryId;
                    if (territoryId >= 0)
                        ConveyorService.MarkTerritoryPending(territoryId);
                }
            }
        }
        finally
        {
            entities.Dispose();
        }
    }
}
