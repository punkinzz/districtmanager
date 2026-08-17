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
using Game.Simulation;
using Game.Tools;
using Game.UI;
using Game.UI.InGame;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Scripting;

namespace DistrictManager.Systems
{
    public partial class DistrictOverviewUISystem : UISystemBase
    {
        public const string kGroup = "districtManager";

        private const float kRefreshIntervalSeconds = 2f;

        private const int kLowHappinessThreshold = 45;

        private EntityQuery m_DistrictQuery;
        private EntityQuery m_DistrictBuildingQuery;
        private EntityQuery m_DistrictPolicyPrefabQuery;
        private EntityQuery m_DistrictServiceBuildingQuery;
        private EntityQuery m_DistrictAssetBuildingQuery;

        private EntityQuery m_CitizenHappinessParameterQuery;
        private EntityQuery m_GarbageParameterQuery;
        private EntityQuery m_HealthcareParameterQuery;
        private EntityQuery m_ParkParameterQuery;
        private EntityQuery m_EducationParameterQuery;
        private EntityQuery m_TelecomParameterQuery;
        private EntityQuery m_HappinessFactorParameterQuery;
        private EntityQuery m_ServiceFeeParameterQuery;

        private PrefabSystem m_PrefabSystem;
        private NameSystem m_NameSystem;
        private GroundPollutionSystem m_GroundPollutionSystem;
        private NoisePollutionSystem m_NoisePollutionSystem;
        private AirPollutionSystem m_AirPollutionSystem;
        private TelecomCoverageSystem m_TelecomCoverageSystem;
        private TaxSystem m_TaxSystem;
        private CitySystem m_CitySystem;
        private LocalEffectSystem m_LocalEffectSystem;

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
            m_GroundPollutionSystem = World.GetOrCreateSystemManaged<GroundPollutionSystem>();
            m_NoisePollutionSystem = World.GetOrCreateSystemManaged<NoisePollutionSystem>();
            m_AirPollutionSystem = World.GetOrCreateSystemManaged<AirPollutionSystem>();
            m_TelecomCoverageSystem = World.GetOrCreateSystemManaged<TelecomCoverageSystem>();
            m_TaxSystem = World.GetOrCreateSystemManaged<TaxSystem>();
            m_CitySystem = World.GetOrCreateSystemManaged<CitySystem>();
            m_LocalEffectSystem = World.GetOrCreateSystemManaged<LocalEffectSystem>();

            m_DistrictQuery = GetEntityQuery(
                ComponentType.ReadOnly<District>(),
                ComponentType.Exclude<Temp>());

            m_DistrictBuildingQuery = GetEntityQuery(
                ComponentType.ReadOnly<Building>(),
                ComponentType.ReadOnly<CurrentDistrict>(),
                ComponentType.ReadOnly<Renter>(),
                ComponentType.ReadOnly<ResidentialProperty>(),
                ComponentType.Exclude<Temp>(),
                ComponentType.Exclude<Deleted>());

            m_DistrictPolicyPrefabQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[] { ComponentType.ReadOnly<PolicyData>() },
                Any = new ComponentType[]
                {
                    ComponentType.ReadOnly<DistrictOptionData>(),
                    ComponentType.ReadOnly<DistrictModifierData>(),
                },
            });

            m_DistrictServiceBuildingQuery = GetEntityQuery(
                ComponentType.ReadOnly<Building>(),
                ComponentType.ReadOnly<CityServiceUpkeep>(),
                ComponentType.ReadOnly<ServiceDistrict>(),
                ComponentType.Exclude<Temp>(),
                ComponentType.Exclude<Deleted>());

            m_CitizenHappinessParameterQuery = GetEntityQuery(ComponentType.ReadOnly<CitizenHappinessParameterData>());
            m_GarbageParameterQuery = GetEntityQuery(ComponentType.ReadOnly<GarbageParameterData>());
            m_HealthcareParameterQuery = GetEntityQuery(ComponentType.ReadOnly<HealthcareParameterData>());
            m_ParkParameterQuery = GetEntityQuery(ComponentType.ReadOnly<ParkParameterData>());
            m_EducationParameterQuery = GetEntityQuery(ComponentType.ReadOnly<EducationParameterData>());
            m_TelecomParameterQuery = GetEntityQuery(ComponentType.ReadOnly<TelecomParameterData>());
            m_HappinessFactorParameterQuery = GetEntityQuery(ComponentType.ReadOnly<HappinessFactorParameterData>());
            m_ServiceFeeParameterQuery = GetEntityQuery(ComponentType.ReadOnly<ServiceFeeParameterData>());

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

            AddBinding(m_Enabled = new ValueBinding<bool>(kGroup, "enabled", Mod.Instance?.Enabled ?? true));

            AddBinding(new TriggerBinding(kGroup, "togglePanel", TogglePanel));
            AddBinding(new TriggerBinding(kGroup, "refresh", ManualRefresh));
        }

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
                Mod.log.Error($"DistrictOverviewUISystem.RefreshDistricts failed: {ex}");
                m_Districts.Clear();
            }
        }

        private void RefreshDistrictsInternal()
        {
            var districtEntities = m_DistrictQuery.ToEntityArray(Allocator.Temp);
            var policyPrefabEntities = m_DistrictPolicyPrefabQuery.ToEntityArray(Allocator.Temp);

            var happinessSum = new Dictionary<Entity, long>();
            var citizenCount = new Dictionary<Entity, int>();
            var factorSum = new Dictionary<Entity, int[]>();
            var factorCount = new Dictionary<Entity, int[]>();

            var citizenHappinessParameters = m_CitizenHappinessParameterQuery.GetSingleton<CitizenHappinessParameterData>();
            var garbageParameters = m_GarbageParameterQuery.GetSingleton<GarbageParameterData>();
            var healthcareParameters = m_HealthcareParameterQuery.GetSingleton<HealthcareParameterData>();
            var parkParameters = m_ParkParameterQuery.GetSingleton<ParkParameterData>();
            var educationParameters = m_EducationParameterQuery.GetSingleton<EducationParameterData>();
            var telecomParameters = m_TelecomParameterQuery.GetSingleton<TelecomParameterData>();
            var serviceFeeParameters = m_ServiceFeeParameterQuery.GetSingleton<ServiceFeeParameterData>();
            var happinessFactorParameters = EntityManager.GetBuffer<HappinessFactorParameterData>(
                m_HappinessFactorParameterQuery.GetSingletonEntity(), true);

            var groundPollutionMap = m_GroundPollutionSystem.GetData(true, out var groundPollutionDeps).m_Buffer;
            var noisePollutionMap = m_NoisePollutionSystem.GetData(true, out var noisePollutionDeps).m_Buffer;
            var airPollutionMap = m_AirPollutionSystem.GetData(true, out var airPollutionDeps).m_Buffer;
            var telecomCoverage = m_TelecomCoverageSystem.GetData(true, out var telecomCoverageDeps);
            groundPollutionDeps.Complete();
            noisePollutionDeps.Complete();
            airPollutionDeps.Complete();
            telecomCoverageDeps.Complete();

            var taxRates = m_TaxSystem.GetTaxRates();
            Entity cityEntity = m_CitySystem.City;
            var cityFees = EntityManager.GetBuffer<ServiceFee>(cityEntity, true);
            float relativeElectricityFee = ServiceFeeSystem.GetFee(PlayerResource.Electricity, cityFees) / serviceFeeParameters.m_ElectricityFee.m_Default;
            float relativeWaterFee = ServiceFeeSystem.GetFee(PlayerResource.Water, cityFees) / serviceFeeParameters.m_WaterFee.m_Default;

            var localEffectData = m_LocalEffectSystem.GetReadData(out var localEffectDeps);
            localEffectDeps.Complete();

            var prefabRefLookup = GetComponentLookup<PrefabRef>(true);
            var spawnableBuildingLookup = GetComponentLookup<SpawnableBuildingData>(true);
            var buildingPropertyLookup = GetComponentLookup<BuildingPropertyData>(true);
            var cityModifierLookup = GetBufferLookup<CityModifier>(true);
            var buildingLookup = GetComponentLookup<Building>(true);
            var electricityConsumerLookup = GetComponentLookup<ElectricityConsumer>(true);
            var waterConsumerLookup = GetComponentLookup<WaterConsumer>(true);
            var serviceCoverageLookup = GetBufferLookup<Game.Net.ServiceCoverage>(true);
            var lockedLookup = GetComponentLookup<Locked>(true);
            var transformLookup = GetComponentLookup<Game.Objects.Transform>(true);
            var garbageProducerLookup = GetComponentLookup<GarbageProducer>(true);
            var crimeProducerLookup = GetComponentLookup<CrimeProducer>(true);
            var mailProducerLookup = GetComponentLookup<MailProducer>(true);
            var renterLookup = GetBufferLookup<Renter>(true);
            var citizenLookup = GetComponentLookup<Citizen>(true);
            var householdCitizenLookup = GetBufferLookup<HouseholdCitizen>(true);
            var buildingDataLookup = GetComponentLookup<BuildingData>(true);

            int factorCountPerBuilding = (int)BuildingHappinessFactor.Count;
            var scratchFactors = new NativeArray<int2>(factorCountPerBuilding, Allocator.Temp);

            var buildingEntities = m_DistrictBuildingQuery.ToEntityArray(Allocator.Temp);
            foreach (var building in buildingEntities)
            {
                Entity district = EntityManager.GetComponentData<CurrentDistrict>(building).m_District;
                if (district == Entity.Null)
                {
                    continue;
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

                        happinessSum[district] = GetOrZero(happinessSum, district) + citizenData.Happiness;
                        citizenCount[district] = GetOrZero(citizenCount, district) + 1;
                    }
                }

                for (int k = 0; k < scratchFactors.Length; k++)
                {
                    scratchFactors[k] = default;
                }

                BuildingHappiness.GetResidentialBuildingHappinessFactors(
                    cityEntity, taxRates, building, scratchFactors,
                    ref prefabRefLookup, ref spawnableBuildingLookup, ref buildingPropertyLookup, ref cityModifierLookup,
                    ref buildingLookup, ref electricityConsumerLookup, ref waterConsumerLookup, ref serviceCoverageLookup,
                    ref lockedLookup, ref transformLookup, ref garbageProducerLookup, ref crimeProducerLookup,
                    ref mailProducerLookup, ref renterLookup, ref citizenLookup, ref householdCitizenLookup,
                    ref buildingDataLookup, ref localEffectData,
                    citizenHappinessParameters, garbageParameters, healthcareParameters, parkParameters,
                    educationParameters, telecomParameters, happinessFactorParameters,
                    groundPollutionMap, noisePollutionMap, airPollutionMap, telecomCoverage,
                    relativeElectricityFee, relativeWaterFee);

                if (!factorSum.TryGetValue(district, out var districtFactorSum))
                {
                    districtFactorSum = new int[factorCountPerBuilding];
                    factorSum[district] = districtFactorSum;
                    factorCount[district] = new int[factorCountPerBuilding];
                }
                var districtFactorCount = factorCount[district];
                for (int k = 0; k < scratchFactors.Length; k++)
                {
                    int2 factorValue = scratchFactors[k];
                    if (factorValue.x <= 0)
                    {
                        continue;
                    }
                    districtFactorSum[k] += factorValue.y;
                    districtFactorCount[k] += factorValue.x;
                }
            }
            scratchFactors.Dispose();
            buildingEntities.Dispose();

            var servicesByDistrict = new Dictionary<Entity, List<ServiceInfo>>();
            var serviceBuildingEntities = m_DistrictServiceBuildingQuery.ToEntityArray(Allocator.Temp);
            foreach (var building in serviceBuildingEntities)
            {
                if (!EntityManager.TryGetBuffer<ServiceDistrict>(building, true, out var assignedDistricts)
                    || assignedDistricts.Length == 0)
                {
                    continue;
                }

                string buildingName = $"{m_NameSystem.GetRenderedLabelName(building)} (#{building.Index})";

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

                factorSum.TryGetValue(district, out var districtFactorSumFinal);
                factorCount.TryGetValue(district, out var districtFactorCountFinal);
                var complaints = BuildTopComplaint(population, averageHappiness, districtFactorSumFinal, districtFactorCountFinal);

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

        private static List<string> BuildTopComplaint(
            int population,
            int averageHappiness,
            int[] factorSum,
            int[] factorCount)
        {
            string topText = null;
            int topWeight = 0;

            void Consider(string text, int weight)
            {
                if (weight < topWeight)
                {
                    topWeight = weight;
                    topText = text;
                }
            }

            if (factorSum != null && factorCount != null)
            {
                for (int i = 0; i < factorSum.Length; i++)
                {
                    if (factorCount[i] <= 0)
                    {
                        continue;
                    }
                    Consider(FactorLabel((BuildingHappinessFactor)i), factorSum[i] / factorCount[i]);
                }
            }

            if (population > 0 && averageHappiness < kLowHappinessThreshold)
            {
                Consider("Low overall citizen happiness", averageHappiness - kLowHappinessThreshold);
            }

            var result = new List<string>();
            if (topText != null)
            {
                result.Add(topText);
            }
            return result;
        }

        private static string FactorLabel(BuildingHappinessFactor factor)
        {
            switch (factor)
            {
                case BuildingHappinessFactor.Telecom: return "Poor telecom coverage";
                case BuildingHappinessFactor.Crime: return "High crime";
                case BuildingHappinessFactor.AirPollution: return "Air pollution";
                case BuildingHappinessFactor.Electricity: return "Unreliable electricity";
                case BuildingHappinessFactor.Healthcare: return "Poor healthcare access";
                case BuildingHappinessFactor.GroundPollution: return "Ground pollution";
                case BuildingHappinessFactor.NoisePollution: return "Noise pollution";
                case BuildingHappinessFactor.Water: return "Unreliable water supply";
                case BuildingHappinessFactor.WaterPollution: return "Water pollution";
                case BuildingHappinessFactor.Sewage: return "Sewage issues";
                case BuildingHappinessFactor.Garbage: return "Garbage buildup";
                case BuildingHappinessFactor.Entertainment: return "Lack of entertainment";
                case BuildingHappinessFactor.Education: return "Poor education access";
                case BuildingHappinessFactor.Mail: return "Mail delivery issues";
                case BuildingHappinessFactor.Welfare: return "Lack of welfare support";
                case BuildingHappinessFactor.Leisure: return "Lack of leisure time";
                case BuildingHappinessFactor.Tax: return "High taxes";
                case BuildingHappinessFactor.Apartment: return "Cramped housing";
                case BuildingHappinessFactor.ElectricityFee: return "High electricity fees";
                case BuildingHappinessFactor.WaterFee: return "High water fees";
                default: return factor.ToString();
            }
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
