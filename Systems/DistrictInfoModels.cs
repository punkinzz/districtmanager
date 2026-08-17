using System.Collections.Generic;
using Colossal.UI.Binding;
using Unity.Entities;

namespace DistrictManager.Systems
{
    public struct PolicyInfo : IJsonWritable
    {
        public string name;
        public bool active;
        public bool hasSlider;
        public float adjustment;

        public void Write(IJsonWriter writer)
        {
            writer.TypeBegin("districtManager.PolicyInfo");
            writer.PropertyName("name");
            writer.Write(name);
            writer.PropertyName("active");
            writer.Write(active);
            writer.PropertyName("hasSlider");
            writer.Write(hasSlider);
            writer.PropertyName("adjustment");
            writer.Write(adjustment);
            writer.TypeEnd();
        }
    }

    public struct ServiceInfo : IJsonWritable
    {
        public string name;
        public Entity entity;

        public void Write(IJsonWriter writer)
        {
            writer.TypeBegin("districtManager.ServiceInfo");
            writer.PropertyName("name");
            writer.Write(name);
            writer.PropertyName("entity");
            writer.Write(entity);
            writer.TypeEnd();
        }
    }

    public struct DistrictInfo : IJsonWritable
    {
        public Entity entity;
        public string name;
        public int population;
        public int averageHappiness;
        public string happinessLabel;
        public List<PolicyInfo> policies;
        public List<ServiceInfo> services;
        public List<ServiceInfo> assets;
        public List<string> complaints;

        public void Write(IJsonWriter writer)
        {
            writer.TypeBegin("districtManager.DistrictInfo");
            writer.PropertyName("entity");
            writer.Write(entity);
            writer.PropertyName("name");
            writer.Write(name);
            writer.PropertyName("population");
            writer.Write(population);
            writer.PropertyName("averageHappiness");
            writer.Write(averageHappiness);
            writer.PropertyName("happinessLabel");
            writer.Write(happinessLabel);

            writer.PropertyName("policies");
            writer.ArrayBegin(policies.Count);
            foreach (var policy in policies)
            {
                policy.Write(writer);
            }
            writer.ArrayEnd();

            writer.PropertyName("services");
            writer.ArrayBegin(services.Count);
            foreach (var service in services)
            {
                service.Write(writer);
            }
            writer.ArrayEnd();

            writer.PropertyName("assets");
            writer.ArrayBegin(assets.Count);
            foreach (var asset in assets)
            {
                asset.Write(writer);
            }
            writer.ArrayEnd();

            writer.PropertyName("complaints");
            writer.ArrayBegin(complaints.Count);
            foreach (var complaint in complaints)
            {
                writer.Write(complaint);
            }
            writer.ArrayEnd();

            writer.TypeEnd();
        }
    }
}
