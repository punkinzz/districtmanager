// Mirrors DistrictInfo/PolicyInfo in Systems/DistrictInfoModels.cs - keep these two in sync.

export interface Entity {
    index: number;
    version: number;
}

export interface PolicyInfo {
    name: string;
    active: boolean;
    hasSlider: boolean;
    adjustment: number;
}

export interface ServiceInfo {
    name: string;
    entity: Entity;
}

export interface DistrictInfo {
    entity: Entity;
    name: string;
    population: number;
    averageHappiness: number;
    happinessLabel: string;
    policies: PolicyInfo[];
    services: ServiceInfo[];
    assets: ServiceInfo[];
    complaints: string[];
}
