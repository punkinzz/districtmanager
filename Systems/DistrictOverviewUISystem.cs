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
    /// <summary>
    /// Backs the "District Manager" toolbar button and panel: gathers, for every real district
    /// in the city, its name, population, average happiness, active policies, explicitly
    /// assigned services, and a short list of top complaints, and exposes it to the UI over a
    /// single value binding.
    ///
    /// All ECS types used here were confirmed by decompiling the installed Game.dll rather than
    /// guessed - see districtmanager/NOTES.md for the source references and the simplifications
    /// called out below. Note: this project targets net48/C# 9 (set by the toolchain's
    /// Mod.props), so newer BCL conveniences like Dictionary.GetValueOrDefault aren't available -
    /// see the small GetOrZero helpers below instead.
    /// </summary>
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

            // All real (non-preview/ghost) districts - same query the vanilla DistrictsSection uses.
            m_DistrictQuery = GetEntityQuery(
                ComponentType.ReadOnly<District>(),
                ComponentType.Exclude<Temp>());

            // Residential buildings tagged with which district they're currently in - same shape
            // as the vanilla AverageHappinessSection's m_DistrictBuildingQuery.
            m_DistrictBuildingQuery = GetEntityQuery(
                ComponentType.ReadOnly<Building>(),
                ComponentType.ReadOnly<CurrentDistrict>(),
                ComponentType.ReadOnly<Renter>(),
                ComponentType.ReadOnly<ResidentialProperty>(),
                ComponentType.Exclude<Temp>(),
                ComponentType.Exclude<Deleted>());

            // Policy prefabs that can apply to a district (mirrors PoliciesUISystem.m_DistrictPoliciesQuery).
            m_DistrictPolicyPrefabQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[] { ComponentType.ReadOnly<PolicyData>() },
                Any = new ComponentType[]
                {
                    ComponentType.ReadOnly<DistrictOptionData>(),
                    ComponentType.ReadOnly<DistrictModifierData>(),
                },
            });

            // City service buildings actually assigned to a district via the vanilla "restrict
            // this service building to a district" tool. Confirmed by decompile that
            // Game.Areas.ServiceDistrict is IBufferElementData { Entity m_District } - a real
            // per-building list of the district(s) it's been explicitly assigned to, not just a
            // marker. Association is read from that buffer's actual entries in
            // RefreshDistrictsInternal, NOT from the building's own CurrentDistrict (physical
            // location) - a building can sit inside a district's borders without being assigned
            // to serve it, and this list must reflect the latter, not the former. This query only
            // needs to pre-filter to buildings that carry the buffer type at all (CityServiceUpkeep
            // narrows it to actual service buildings, matching what the assignment tool targets);
            // an entity can have the component with zero entries, so an empty buffer must still be
            // checked and skipped per-building, not assumed away by this query. See NOTES.md.
            m_DistrictServiceBuildingQuery = GetEntityQuery(
                ComponentType.ReadOnly<Building>(),
                ComponentType.ReadOnly<CityServiceUpkeep>(),
                ComponentType.ReadOnly<ServiceDistrict>(),
                ComponentType.Exclude<Temp>(),
                ComponentType.Exclude<Deleted>());

            // "Assets" - parks and signature/landmark buildings physically located in a district.
            // These are never district-assignable (no ServiceDistrict buffer at all), which is
            // exactly why they were wrongly showing up in Services before this was split out -
            // Park/Signature buildings still carry CityServiceUpkeep in some cases, so the
            // None: ServiceDistrict exclusion below is what keeps this list and the Services list
            // from double-counting the same building.
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

            // Reflects Setting.Enabled from the mod's Options page - the TS side hides the
            // toolbar button entirely (and the panel force-closes) when this is false.
            AddBinding(m_Enabled = new ValueBinding<bool>(kGroup, "enabled", Mod.Instance?.Enabled ?? true));

            AddBinding(new TriggerBinding(kGroup, "togglePanel", TogglePanel));

            // Manual refresh - lets the player force an update without waiting for the
            // kRefreshIntervalSeconds tick while the panel's already open.
            AddBinding(new TriggerBinding(kGroup, "refresh", ManualRefresh));
        }

        // Without this, m_Districts (and whatever the JS side last saw) would survive a save
        // load untouched, showing the previous city's districts in the new one - the same reason
        // the vanilla PoliciesUISystem clears its own cached lists here.
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

        // Mod.Instance may not exist yet when this system's OnCreate runs - ECS auto-creates
        // systems independently of Mod.OnLoad's own ordering - so the subscription happens
        // lazily here on the first update where it's available instead.
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
                // Refresh immediately on open rather than waiting for the next tick, so the
                // panel never shows a frame of stale/empty data.
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
                // Errors here are otherwise silent (Mod.log suppresses UI error popups), which
                // previously meant a single bad entry could quietly leave the panel empty. Log
                // it so it's actually diagnosable, and leave m_Districts cleared rather than
                // half-populated.
                Mod.log.Error($"DistrictOverviewUISystem.RefreshDistricts failed: {ex}");
                m_Districts.Clear();
            }
        }

        private void RefreshDistrictsInternal()
        {
            var districtEntities = m_DistrictQuery.ToEntityArray(Allocator.Temp);
            var policyPrefabEntities = m_DistrictPolicyPrefabQuery.ToEntityArray(Allocator.Temp);

            // --- Pass 1: aggregate per-district happiness/population/crime/garbage from buildings ---
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

                        // Note: unlike the vanilla AverageHappinessSection, this doesn't exclude
                        // dead/departed citizens via HealthProblem - a minor, documented v1
                        // simplification (see NOTES.md).
                        happinessSum[district] = GetOrZero(happinessSum, district) + citizenData.Happiness;
                        citizenCount[district] = GetOrZero(citizenCount, district) + 1;
                    }
                }
            }
            buildingEntities.Dispose();

            // City-wide per-building averages, used to flag a district's crime/garbage as
            // "above average" rather than against an arbitrary absolute number.
            float cityAvgCrimePerBuilding = SafeAverage(SumAll(crimeSum), SumAll(buildingCount));
            float cityAvgGarbagePerBuilding = SafeAverage(SumAll(garbageSum), SumAll(buildingCount));

            // --- Pass 2: city service buildings actually assigned to each district ---
            var servicesByDistrict = new Dictionary<Entity, List<ServiceInfo>>();
            var serviceBuildingEntities = m_DistrictServiceBuildingQuery.ToEntityArray(Allocator.Temp);
            foreach (var building in serviceBuildingEntities)
            {
                // The query only guarantees the buffer *type* is present - most service buildings
                // have never been assigned to a district via the vanilla tool, so their buffer is
                // simply empty. Those correctly show under no district at all, in no section of
                // the panel (this is deliberate - see the query comment in OnCreate).
                if (!EntityManager.TryGetBuffer<ServiceDistrict>(building, true, out var assignedDistricts)
                    || assignedDistricts.Length == 0)
                {
                    continue;
                }

                // Service buildings (unlike zoned/addressed ones) generally render via their
                // prefab's generic title - e.g. every "Small Medical Clinic" in the city shows
                // that exact same string, not a per-building name. That reads as if the same
                // building were showing up under multiple districts, when really it's two
                // distinct buildings sharing a non-unique label. Appending the entity index
                // disambiguates them; BuildingUtils.GetAddress could give a nicer
                // "(Road name, number)" suffix instead, but needs more verification of which
                // component it depends on before relying on it.
                string buildingName = $"{m_NameSystem.GetRenderedLabelName(building)} (#{building.Index})";

                // A building's assignment buffer can list more than one district (the tool allows
                // assigning a service building to serve several districts at once) - list it under
                // every district it's actually assigned to, not just one.
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

            // --- Pass 2b: parks/signature buildings physically located in each district ---
            // (kept separate from Services above, since these are never district-assignable -
            // see the query comment in OnCreate and NOTES.md).
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

            // --- Pass 3: assemble the final per-district entries ---
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

                var complaints = BuildTopComplaint(population, averageHappiness, services.Count, avgCrime,
                    cityAvgCrimePerBuilding, avgGarbage, cityAvgGarbagePerBuilding);

                m_Districts.Add(new DistrictInfo
                {
                    entity = district,
                    name = m_NameSystem.GetRenderedLabelName(district),
                    population = population,
                    averageHappiness = averageHappiness,
                    happinessLabel = HappinessLabel(averageHappiness),
                    policies = policies,
                    services = services,
                    assets = assets,
                    complaints = complaints,
                });
            }

            policyPrefabEntities.Dispose();
            districtEntities.Dispose();
        }

        // A moderate baseline severity for "no services" so it can be ranked against the other,
        // numeric complaint candidates below - a documented judgment call, not a measured value.
        private const float kNoServicesSeverity = 20f;

        // Only the single worst complaint is shown (per user request), ranked by a severity score
        // that's comparable across categories even though the underlying units aren't: happiness
        // is a 0-100 gap below threshold, crime/garbage are a percentage above the city average,
        // and "no services" is a fixed baseline (see kNoServicesSeverity above).
        private static List<string> BuildTopComplaint(
            int population,
            int averageHappiness,
            int serviceCount,
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
            if (serviceCount == 0)
            {
                Consider("No city services located in this district", kNoServicesSeverity);
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
                // Read visibility straight off the ECS component rather than going through
                // PrefabSystem.TryGetPrefab<PolicyPrefab> - PolicyPrefab is an abstract class and
                // resolving it generically per-entity isn't worth the risk when the component
                // data has everything we need already.
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

        private static string HappinessLabel(int averageHappiness)
        {
            if (averageHappiness >= 80) return "Great";
            if (averageHappiness >= 60) return "Good";
            if (averageHappiness >= 40) return "Average";
            if (averageHappiness >= 20) return "Bad";
            return "Terrible";
        }
    }
}
