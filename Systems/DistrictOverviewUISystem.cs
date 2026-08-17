using System;
using System.Collections.Generic;
using Colossal.Entities;
using Colossal.Serialization.Entities;
using Colossal.UI.Binding;
using Game.Areas;
using Game.Buildings;
using Game.Citizens;
using Game.City;
using Game.Common;
using Game.Policies;
using Game.Prefabs;
using Game.Tools;
using Game.UI;
using Unity.Collections;
using Unity.Entities;
using UnityEngine.Scripting;

namespace DistrictManager.Systems
{
    // Backs the toolbar button + panel. Gathers per-district name/population/happiness/policies/
    // services/complaints and pushes it to the UI as one binding.
    // (targeting net48/C# 9 here, so no Dictionary.GetValueOrDefault - hence the GetOrZero helpers)
    public partial class DistrictOverviewUISystem : UISystemBase
    {
        public const string kGroup = "districtManager";

        // Only refresh the (moderately expensive) district scan on this cadence, and only while
        // the panel is actually open - no point paying for it while the player has it closed.
        private const float kRefreshIntervalSeconds = 2f;

        // Below this average happiness (0-100), a district is called out for it in complaints.
        private const int kLowHappinessThreshold = 45;

        private EntityQuery m_DistrictQuery;
        private EntityQuery m_DistrictBuildingQuery;
        private EntityQuery m_DistrictPolicyPrefabQuery;
        private EntityQuery m_DistrictServiceBuildingQuery;
        private EntityQuery m_DistrictAssetBuildingQuery;

        private PrefabSystem m_PrefabSystem;
        private NameSystem m_NameSystem;

        private readonly List<DistrictInfo> m_Districts = new List<DistrictInfo>();
        private GetterValueBinding<List<DistrictInfo>> m_DistrictsBinding;
        private ValueBinding<bool> m_PanelOpen;
        private ValueBinding<bool> m_Enabled;
        private bool m_SubscribedToSettings;

        private float m_RefreshTimer;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();

            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_NameSystem = World.GetOrCreateSystemManaged<NameSystem>();

            // all real districts (no previews/ghosts)
            m_DistrictQuery = GetEntityQuery(
                ComponentType.ReadOnly<District>(),
                ComponentType.Exclude<Temp>());

            // residential buildings tagged with their current district
            m_DistrictBuildingQuery = GetEntityQuery(
                ComponentType.ReadOnly<Building>(),
                ComponentType.ReadOnly<CurrentDistrict>(),
                ComponentType.ReadOnly<Renter>(),
                ComponentType.ReadOnly<ResidentialProperty>(),
                ComponentType.Exclude<Temp>(),
                ComponentType.Exclude<Deleted>());

            // policy prefabs that can apply to a district
            m_DistrictPolicyPrefabQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[] { ComponentType.ReadOnly<PolicyData>() },
                Any = new ComponentType[]
                {
                    ComponentType.ReadOnly<DistrictOptionData>(),
                    ComponentType.ReadOnly<DistrictModifierData>(),
                },
            });

            // Service buildings actually assigned to a district via the game's own "restrict to
            // district" tool. ServiceDistrict is a per-building buffer of assigned districts, not
            // just a marker - most buildings have the buffer but it's empty, so RefreshDistrictsInternal
            // still has to check length, not just presence. Deliberately NOT using CurrentDistrict
            // (physical location) here - a building inside a district's borders isn't necessarily
            // assigned to serve it.
            m_DistrictServiceBuildingQuery = GetEntityQuery(
                ComponentType.ReadOnly<Building>(),
                ComponentType.ReadOnly<CityServiceUpkeep>(),
                ComponentType.ReadOnly<ServiceDistrict>(),
                ComponentType.Exclude<Temp>(),
                ComponentType.Exclude<Deleted>());

            // Assets: parks/signature buildings physically in a district. Excluding ServiceDistrict
            // so these don't double up with Services (some parks carry CityServiceUpkeep too).
            m_DistrictAssetBuildingQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[] { ComponentType.ReadOnly<Building>(), ComponentType.ReadOnly<CurrentDistrict>() },
                Any = new ComponentType[]
                {
                    ComponentType.ReadOnly<Game.Buildings.Park>(),
                    ComponentType.ReadOnly<Signature>(),
                    ComponentType.ReadOnly<CityServiceUpkeep>(),
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<ServiceDistrict>(),
                },
            });

            AddBinding(m_DistrictsBinding = new GetterValueBinding<List<DistrictInfo>>(
                kGroup,
                "districts",
                () => m_Districts,
                new DelegateWriter<List<DistrictInfo>>(WriteDistrictList)));

            AddBinding(m_PanelOpen = new ValueBinding<bool>(kGroup, "panelOpen", false));

            // mirrors Setting.Enabled - TS side hides the toolbar button when false
            AddBinding(m_Enabled = new ValueBinding<bool>(kGroup, "enabled", Mod.Instance?.Enabled ?? true));

            AddBinding(new TriggerBinding(kGroup, "togglePanel", TogglePanel));
            AddBinding(new TriggerBinding(kGroup, "refresh", ManualRefresh));
        }

        // clear cached districts on save load, otherwise the old city's data lingers
        [Preserve]
        protected override void OnGameLoaded(Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);

            m_RefreshTimer = 0f;
            m_Districts.Clear();
            m_DistrictsBinding.Update();

            if (m_PanelOpen.value)
            {
                RefreshDistricts();
                m_DistrictsBinding.Update();
            }
        }

        private void ManualRefresh()
        {
            if (!m_Enabled.value || !m_PanelOpen.value)
            {
                return;
            }

            m_RefreshTimer = 0f;
            RefreshDistricts();
            m_DistrictsBinding.Update();
        }

        [Preserve]
        protected override void OnDestroy()
        {
            if (m_SubscribedToSettings && Mod.Instance != null)
            {
                Mod.Instance.onSettingsApplied -= OnSettingsApplied;
            }
            base.OnDestroy();
        }

        // Mod.Instance can be null when OnCreate runs (ECS creates systems on its own schedule),
        // so subscribe lazily on first update instead.
        private void TrySubscribeToSettings()
        {
            if (m_SubscribedToSettings || Mod.Instance == null)
            {
                return;
            }

            Mod.Instance.onSettingsApplied += OnSettingsApplied;
            m_SubscribedToSettings = true;
            m_Enabled.Update(Mod.Instance.Enabled);
        }

        private void OnSettingsApplied(Game.Settings.Setting setting)
        {
            if (!(setting is Setting districtManagerSetting))
            {
                return;
            }

            m_Enabled.Update(districtManagerSetting.Enabled);
            if (!districtManagerSetting.Enabled && m_PanelOpen.value)
            {
                m_PanelOpen.Update(false);
            }
        }

        private static void WriteDistrictList(IJsonWriter writer, List<DistrictInfo> list)
        {
            writer.ArrayBegin(list.Count);
            foreach (var district in list)
            {
                district.Write(writer);
            }
            writer.ArrayEnd();
        }

        private void TogglePanel()
        {
            if (!m_Enabled.value)
            {
                return;
            }

            bool nowOpen = !m_PanelOpen.value;
            m_PanelOpen.Update(nowOpen);

            if (nowOpen)
            {
                // refresh right away instead of waiting for the next tick
                m_RefreshTimer = 0f;
                RefreshDistricts();
                m_DistrictsBinding.Update();
            }
        }

        [Preserve]
        protected override void OnUpdate()
        {
            base.OnUpdate();

            TrySubscribeToSettings();

            if (!m_Enabled.value)
            {
                if (m_PanelOpen.value)
                {
                    m_PanelOpen.Update(false);
                }
                return;
            }

            if (!m_PanelOpen.value)
            {
                return;
            }

            m_RefreshTimer += UnityEngine.Time.deltaTime;
            if (m_RefreshTimer < kRefreshIntervalSeconds)
            {
                return;
            }

            m_RefreshTimer = 0f;
            RefreshDistricts();
            m_DistrictsBinding.Update();
        }

        private void RefreshDistricts()
        {
            m_Districts.Clear();

            try
            {
                RefreshDistrictsInternal();
            }
            catch (Exception ex)
            {
                // Mod.log suppresses UI error popups, so log explicitly or a bad entry just
                // leaves the panel silently empty
                Mod.log.Error($"DistrictOverviewUISystem.RefreshDistricts failed: {ex}");
                m_Districts.Clear();
            }
        }

        private void RefreshDistrictsInternal()
        {
            var districtEntities = m_DistrictQuery.ToEntityArray(Allocator.Temp);
            var policyPrefabEntities = m_DistrictPolicyPrefabQuery.ToEntityArray(Allocator.Temp);

            // aggregate per-district happiness/population/crime/garbage from buildings
            var happinessSum = new Dictionary<Entity, long>();
            var citizenCount = new Dictionary<Entity, int>();
            var crimeSum = new Dictionary<Entity, float>();
            var garbageSum = new Dictionary<Entity, long>();
            var buildingCount = new Dictionary<Entity, int>();

            var buildingEntities = m_DistrictBuildingQuery.ToEntityArray(Allocator.Temp);
            foreach (var building in buildingEntities)
            {
                Entity district = EntityManager.GetComponentData<CurrentDistrict>(building).m_District;
                if (district == Entity.Null)
                {
                    continue;
                }

                buildingCount[district] = GetOrZero(buildingCount, district) + 1;

                if (EntityManager.TryGetComponent<CrimeProducer>(building, out var crime))
                {
                    crimeSum[district] = GetOrZero(crimeSum, district) + crime.m_Crime;
                }

                if (EntityManager.TryGetComponent<GarbageProducer>(building, out var garbage))
                {
                    garbageSum[district] = GetOrZero(garbageSum, district) + garbage.m_Garbage;
                }

                if (!EntityManager.TryGetBuffer<Renter>(building, true, out var renters))
                {
                    continue;
                }

                for (int i = 0; i < renters.Length; i++)
                {
                    Entity household = renters[i].m_Renter;
                    if (!EntityManager.TryGetBuffer<HouseholdCitizen>(household, true, out var residents))
                    {
                        continue;
                    }

                    for (int j = 0; j < residents.Length; j++)
                    {
                        Entity citizen = residents[j].m_Citizen;
                        if (!EntityManager.TryGetComponent<Citizen>(citizen, out var citizenData))
                        {
                            continue;
                        }

                        // doesn't exclude dead/departed citizens like the vanilla panel does - fine for now
                        happinessSum[district] = GetOrZero(happinessSum, district) + citizenData.Happiness;
                        citizenCount[district] = GetOrZero(citizenCount, district) + 1;
                    }
                }
            }
            buildingEntities.Dispose();

            float cityAvgCrimePerBuilding = SafeAverage(SumAll(crimeSum), SumAll(buildingCount));
            float cityAvgGarbagePerBuilding = SafeAverage(SumAll(garbageSum), SumAll(buildingCount));

            // service buildings actually assigned to a district
            var servicesByDistrict = new Dictionary<Entity, List<ServiceInfo>>();
            var serviceBuildingEntities = m_DistrictServiceBuildingQuery.ToEntityArray(Allocator.Temp);
            foreach (var building in serviceBuildingEntities)
            {
                // most service buildings carry the buffer but it's never been filled in - skip those
                if (!EntityManager.TryGetBuffer<ServiceDistrict>(building, true, out var assignedDistricts)
                    || assignedDistricts.Length == 0)
                {
                    continue;
                }

                // service buildings share a generic prefab title (every "Small Medical Clinic" looks
                // the same), so append the entity index or two different buildings look like one
                string buildingName = $"{m_NameSystem.GetRenderedLabelName(building)} (#{building.Index})";

                // a building can be assigned to more than one district at once
                for (int i = 0; i < assignedDistricts.Length; i++)
                {
                    Entity district = assignedDistricts[i].m_District;
                    if (district == Entity.Null)
                    {
                        continue;
                    }

                    if (!servicesByDistrict.TryGetValue(district, out var list))
                    {
                        list = new List<ServiceInfo>();
                        servicesByDistrict[district] = list;
                    }
                    list.Add(new ServiceInfo { name = buildingName, entity = building });
                }
            }
            serviceBuildingEntities.Dispose();

            // parks/signature buildings physically located in each district
            var assetsByDistrict = new Dictionary<Entity, List<ServiceInfo>>();
            var assetBuildingEntities = m_DistrictAssetBuildingQuery.ToEntityArray(Allocator.Temp);
            foreach (var building in assetBuildingEntities)
            {
                Entity district = EntityManager.GetComponentData<CurrentDistrict>(building).m_District;
                if (district == Entity.Null)
                {
                    continue;
                }

                string buildingName = $"{m_NameSystem.GetRenderedLabelName(building)} (#{building.Index})";
                if (!assetsByDistrict.TryGetValue(district, out var list))
                {
                    list = new List<ServiceInfo>();
                    assetsByDistrict[district] = list;
                }
                list.Add(new ServiceInfo { name = buildingName, entity = building });
            }
            assetBuildingEntities.Dispose();

            // assemble the final per-district entries
            foreach (var district in districtEntities)
            {
                int population = GetOrZero(citizenCount, district);
                long happinessTotal = GetOrZero(happinessSum, district);
                int averageHappiness = population > 0 ? (int)Math.Round(happinessTotal / (double)population) : 0;

                var policies = GatherDistrictPolicies(district, policyPrefabEntities);
                List<ServiceInfo> services;
                if (!servicesByDistrict.TryGetValue(district, out services))
                {
                    services = new List<ServiceInfo>();
                }

                List<ServiceInfo> assets;
                if (!assetsByDistrict.TryGetValue(district, out assets))
                {
                    assets = new List<ServiceInfo>();
                }

                int buildings = GetOrZero(buildingCount, district);
                float avgCrime = SafeAverage(GetOrZero(crimeSum, district), buildings);
                float avgGarbage = SafeAverage(GetOrZero(garbageSum, district), buildings);

                var complaints = BuildTopComplaint(population, averageHappiness, avgCrime,
                    cityAvgCrimePerBuilding, avgGarbage, cityAvgGarbagePerBuilding);

                m_Districts.Add(new DistrictInfo
                {
                    entity = district,
                    name = m_NameSystem.GetRenderedLabelName(district),
                    population = population,
                    averageHappiness = averageHappiness,
                    happinessLabel = HappinessLabel(population, averageHappiness),
                    policies = policies,
                    services = services,
                    assets = assets,
                    complaints = complaints,
                });
            }

            policyPrefabEntities.Dispose();
            districtEntities.Dispose();
        }

        // only the single worst complaint is shown, scored on a rough comparable scale even
        // though the units differ (happiness gap, % above city average)
        private static List<string> BuildTopComplaint(
            int population,
            int averageHappiness,
            float avgCrime,
            float cityAvgCrimePerBuilding,
            float avgGarbage,
            float cityAvgGarbagePerBuilding)
        {
            string topText = null;
            float topSeverity = float.NegativeInfinity;

            void Consider(string text, float severity)
            {
                if (severity > topSeverity)
                {
                    topSeverity = severity;
                    topText = text;
                }
            }

            if (population > 0 && averageHappiness < kLowHappinessThreshold)
            {
                Consider("Low overall citizen happiness", kLowHappinessThreshold - averageHappiness);
            }
            if (avgCrime > 0f && cityAvgCrimePerBuilding > 0f && avgCrime > cityAvgCrimePerBuilding)
            {
                Consider("Crime reports above the city average", (avgCrime - cityAvgCrimePerBuilding) / cityAvgCrimePerBuilding * 100f);
            }
            if (avgGarbage > 0f && cityAvgGarbagePerBuilding > 0f && avgGarbage > cityAvgGarbagePerBuilding)
            {
                Consider("Garbage buildup above the city average", (avgGarbage - cityAvgGarbagePerBuilding) / cityAvgGarbagePerBuilding * 100f);
            }

            var result = new List<string>();
            if (topText != null)
            {
                result.Add(topText);
            }
            return result;
        }

        private List<PolicyInfo> GatherDistrictPolicies(Entity district, NativeArray<Entity> policyPrefabEntities)
        {
            var result = new List<PolicyInfo>();

            if (!EntityManager.TryGetBuffer<Policy>(district, true, out var activePolicies))
            {
                return result;
            }

            foreach (var policyPrefabEntity in policyPrefabEntities)
            {
                // reading straight off the component - PolicyPrefab is abstract, not worth resolving via PrefabSystem
                if (!EntityManager.TryGetComponent<PolicyData>(policyPrefabEntity, out var policyData))
                {
                    continue;
                }
                if (policyData.m_Visibility == (int)PolicyVisibility.HideFromPolicyList)
                {
                    continue;
                }

                bool active = false;
                float adjustment = 0f;
                for (int i = 0; i < activePolicies.Length; i++)
                {
                    if (activePolicies[i].m_Policy == policyPrefabEntity)
                    {
                        active = (activePolicies[i].m_Flags & PolicyFlags.Active) != 0;
                        adjustment = activePolicies[i].m_Adjustment;
                        break;
                    }
                }

                // Only show policies currently in effect - not every policy that could apply.
                if (!active)
                {
                    continue;
                }

                bool hasSlider = EntityManager.HasComponent<PolicySliderData>(policyPrefabEntity);

                result.Add(new PolicyInfo
                {
                    name = m_PrefabSystem.GetPrefabName(policyPrefabEntity),
                    active = true,
                    hasSlider = hasSlider,
                    adjustment = adjustment,
                });
            }

            return result;
        }

        private static long GetOrZero(Dictionary<Entity, long> dict, Entity key)
        {
            long value;
            return dict.TryGetValue(key, out value) ? value : 0L;
        }

        private static int GetOrZero(Dictionary<Entity, int> dict, Entity key)
        {
            int value;
            return dict.TryGetValue(key, out value) ? value : 0;
        }

        private static float GetOrZero(Dictionary<Entity, float> dict, Entity key)
        {
            float value;
            return dict.TryGetValue(key, out value) ? value : 0f;
        }

        private static float SafeAverage(float total, int count) => count > 0 ? total / count : 0f;

        private static float SafeAverage(long total, int count) => count > 0 ? total / (float)count : 0f;

        private static float SumAll(Dictionary<Entity, float> values)
        {
            float sum = 0f;
            foreach (var value in values.Values)
            {
                sum += value;
            }
            return sum;
        }

        private static long SumAll(Dictionary<Entity, long> values)
        {
            long sum = 0;
            foreach (var value in values.Values)
            {
                sum += value;
            }
            return sum;
        }

        private static int SumAll(Dictionary<Entity, int> values)
        {
            int sum = 0;
            foreach (var value in values.Values)
            {
                sum += value;
            }
            return sum;
        }

        private static string HappinessLabel(int population, int averageHappiness)
        {
            if (population <= 0) return "No residents";
            if (averageHappiness >= 80) return "Great";
            if (averageHappiness >= 60) return "Good";
            if (averageHappiness >= 40) return "Average";
            if (averageHappiness >= 20) return "Bad";
            return "Terrible";
        }
    }
}
