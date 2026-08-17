import { Entity } from "./district-manager-types";

// the .d.ts says focusEntity is a top-level export, but at runtime it actually lives under
// window["cs2/bindings"].camera - a typed import resolves to undefined, so reach in manually

export function focusEntityOnMap(entity: Entity): void {
    const bindingsModule = (window as any)["cs2/bindings"];
    bindingsModule?.camera?.focusEntity?.(entity);
}
