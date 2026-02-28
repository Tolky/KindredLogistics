using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.CastleBuilding;
using ProjectM.Network;
using ProjectM.Terrain;
using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace KindredLogistics.Services
{
    internal class TerritoryService
    {
        readonly Dictionary<WorldRegionType, List<Entity>> territories = [];
        readonly Dictionary<Entity, int> territoryCache = [];

        readonly List<Func<int, Entity, IEnumerator>> territoryUpdateCallbacks = [];

        public const int MIN_TERRITORY_ID = 0;
        public const int MAX_TERRITORY_ID = 146;

        readonly Dictionary<int, Entity> territoryToCastleHeart = [];
        readonly HashSet<int> territoriesRebuilding = [];

        readonly float timeBudget;

        /// <summary>PlatformId of the current territory's owner, resolved once per territory in UpdateLoop.</summary>
        internal ulong CurrentOwnerPlatformId;

        public TerritoryService()
        {
            // Load Territories
            var entityQueryBuilder = new EntityQueryBuilder(Allocator.Temp);
            entityQueryBuilder.AddAll(new(Il2CppType.Of<CastleTerritory>(), ComponentType.AccessMode.ReadWrite));

            var query = Core.EntityManager.CreateEntityQuery(ref entityQueryBuilder);
            entityQueryBuilder.Dispose();

            foreach (var territoryEntity in query.ToEntityArray(Allocator.Temp))
            {
                var region = territoryEntity.Read<TerritoryWorldRegion>().Region;

                if (!territories.TryGetValue(region, out var territoriesInRegion))
                {
                    territoriesInRegion = [];
                    territories[region] = territoriesInRegion;
                }
                territoriesInRegion.Add(territoryEntity);
            }

            entityQueryBuilder = new EntityQueryBuilder(Allocator.Temp)
                .AddAll(new(Il2CppType.Of<CastleHeart>(), ComponentType.AccessMode.ReadOnly));

            var castleHeartQuery = Core.EntityManager.CreateEntityQuery(ref entityQueryBuilder);
            entityQueryBuilder.Dispose();

            var castleHeartEntities = castleHeartQuery.ToEntityArray(Allocator.Temp);
            try
            {
                territoryToCastleHeart.Clear();
                foreach (var castleHeartEntity in castleHeartEntities)
                {
                    var castleHeart = castleHeartEntity.Read<CastleHeart>();
                    var territoryEntity = castleHeart.CastleTerritoryEntity;
                    var territory = territoryEntity.Read<CastleTerritory>();
                    territoryToCastleHeart[territory.CastleTerritoryIndex] = castleHeartEntity;
                }
            }
            finally
            {
                castleHeartEntities.Dispose();
            }
            castleHeartQuery.Dispose();

            int serverFps = SettingsManager.ServerHostSettings.ServerFps;
            timeBudget = (1f / serverFps) * 0.15f;

            Core.StartCoroutine(UpdateLoop());
        }

        public void RegisterTerritoryUpdateCallback(Func<int, Entity, IEnumerator> callback)
        {
            territoryUpdateCallbacks.Add(callback);
        }

        float startTime = 0;
        void StartTimer()
        {
            startTime = Time.realtimeSinceStartup;
        }

        internal bool ShouldUpdateYield()
        {
            return Time.realtimeSinceStartup - startTime > timeBudget;
        }

        IEnumerator UpdateLoop()
        {
            yield return null;
            while (true)
            {
                yield return null;
                StartTimer();

                for (int i = MIN_TERRITORY_ID; i <= MAX_TERRITORY_ID; i++)
                {
                    var castleHeartEntity = GetCastleHeart(i);
                    if (castleHeartEntity == Entity.Null)
                        continue;

                    if (territoriesRebuilding.Contains(i)) continue;
                    if (castleHeartEntity.Read<CastleRebuildPhaseState>().State != PhaseState.None) continue;

                    // Resolve owner once per territory — callbacks use CurrentOwnerPlatformId
                    var ownerEntity = castleHeartEntity.Read<UserOwner>().Owner.GetEntityOnServer();
                    CurrentOwnerPlatformId = ownerEntity != Entity.Null ? ownerEntity.Read<User>().PlatformId : 0UL;

                    // Can't resolve owner yet (not loaded) — skip but DON'T consume pending
                    if (CurrentOwnerPlatformId == 0UL) continue;

                    // Skip all callbacks if every feature is disabled for this owner
                    if (!Core.PlayerSettings.IsConveyorEnabled(CurrentOwnerPlatformId) &&
                        !Core.PlayerSettings.IsSalvageEnabled(CurrentOwnerPlatformId) &&
                        !Core.PlayerSettings.IsUnitSpawnerEnabled(CurrentOwnerPlatformId) &&
                        !Core.PlayerSettings.IsBrazierEnabled(CurrentOwnerPlatformId))
                    {
                        ConveyorService.ConsumePending(i);
                        continue;
                    }

                    foreach (var callback in territoryUpdateCallbacks)
                    {
                        IEnumerator enumerator = null;
                        bool stillRunning = false;
                        try
                        {
                            enumerator = callback(i, castleHeartEntity);
                            stillRunning = enumerator.MoveNext();
                        }
                        catch (Exception e)
                        {
                            Core.LogException(e);
                        }

                        while (stillRunning)
                        {
                            yield return null;
                            StartTimer();

                            try
                            {
                                stillRunning = enumerator.MoveNext();
                            }
                            catch (Exception e)
                            {
                                Core.LogException(e);
                            }
                        }

                        if (ShouldUpdateYield())
                        {
                            yield return null;
                            StartTimer();
                        }
                    }
                }
            }
        }

        public Entity GetCastleHeart(int territoryId)
        {
            if (!territoryToCastleHeart.TryGetValue(territoryId, out var castleHeartEntity))
                return Entity.Null;

            if (!Core.EntityManager.Exists(castleHeartEntity))
            {
                territoryToCastleHeart.Remove(territoryId);
                territoriesRebuilding.Remove(territoryId);
                return Entity.Null;
            }

            var castleHeart = castleHeartEntity.Read<CastleHeart>();
            var territoryEntity = castleHeart.CastleTerritoryEntity;
            if (castleHeart.CastleTerritoryEntity == Entity.Null || !Core.EntityManager.Exists(territoryEntity))
            {
                territoryToCastleHeart.Remove(territoryId);
                territoriesRebuilding.Remove(territoryId);
                return Entity.Null;
            }

            var territory = territoryEntity.Read<CastleTerritory>();
            if (territory.CastleTerritoryIndex != territoryId)
            {
                territoryToCastleHeart.Remove(territoryId);
                territoriesRebuilding.Remove(territoryId);
                AddCastleHeart(castleHeartEntity);
                return Entity.Null;
            }

            return castleHeartEntity;
        }

        internal void AddCastleHeart(Entity castleHeartEntity)
        {
            if (!Core.EntityManager.Exists(castleHeartEntity)) return;
            var castleHeart = castleHeartEntity.Read<CastleHeart>();
            var territoryEntity = castleHeart.CastleTerritoryEntity;
            if (!Core.EntityManager.Exists(territoryEntity)) return;
            var territory = territoryEntity.Read<CastleTerritory>();
            territoryToCastleHeart[territory.CastleTerritoryIndex] = castleHeartEntity;
        }

        internal void RemoveCastleHeart(Entity castleHeartEntity)
        {
            if (!Core.EntityManager.Exists(castleHeartEntity)) return;
            var castleHeart = castleHeartEntity.Read<CastleHeart>();
            var territoryEntity = castleHeart.CastleTerritoryEntity;
            if (!Core.EntityManager.Exists(territoryEntity)) return;
            var territory = territoryEntity.Read<CastleTerritory>();
            territoryToCastleHeart.Remove(territory.CastleTerritoryIndex);
            territoriesRebuilding.Remove(territory.CastleTerritoryIndex);
        }

        public void FlushTerritoryCache()
        {
            territoryCache.Clear();
        }

        public int GetTerritoryId(Entity entity)
        {
            if (territoryCache.TryGetValue(entity, out var territoryId))
            {
                return territoryId;
            }

            if (entity.Has<CastleHeartConnection>())
            {
                var heart = entity.Read<CastleHeartConnection>().CastleHeartEntity.GetEntityOnServer();

                if (Core.EntityManager.Exists(heart) && heart != Entity.Null)
                {
                    var castleHeart = heart.Read<CastleHeart>();
                    var castleTerritory = castleHeart.CastleTerritoryEntity;

                    // Cache the territory id of buildings as they don't change
                    if (castleTerritory.Has<CastleTerritory>())
                    {
                        territoryId = castleTerritory.Read<CastleTerritory>().CastleTerritoryIndex;
                        territoryCache[entity] = territoryId;
                        return territoryId;
                    }
                }
            }

            if (entity.Has<TilePosition>())
            {
                var region = Core.RegionService.GetRegion(entity);
                var tilePos = entity.Read<TilePosition>();
                if (territories.TryGetValue(region, out var territoriesInRegion))
                {
                    for (int i = 0; i < territoriesInRegion.Count; i++)
                    {
                        var territory = territoriesInRegion[i];
                        if (CastleTerritoryExtensions.IsTileInTerritory(Core.EntityManager, tilePos.Tile, ref territory, out var _))
                        {
                            if (territory.Has<CastleTerritory>()) return territory.Read<CastleTerritory>().CastleTerritoryIndex;
                        }
                    }
                }
            }
            return -1;
        }

        public void MarkTerritoryRebuilding(int territoryId)
        {
            Core.Log.LogInfo($"Marking territory {territoryId} as rebuilding.");
            territoriesRebuilding.Add(territoryId);
        }
    }
}
