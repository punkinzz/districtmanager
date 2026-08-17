import { Entity } from "./district-manager-types";

export function focusEntityOnMap(entity: Entity): void {
    const bindingsModule = (window as any)["cs2/bindings"];
    bindingsModule?.camera?.focusEntity?.(entity);
}
