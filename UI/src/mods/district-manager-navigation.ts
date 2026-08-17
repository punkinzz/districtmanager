import { Entity } from "./district-manager-types";

// cs2/bindings' actual runtime shape (window["cs2/bindings"]) nests focusEntity under a
// `camera` namespace object - confirmed live via the Gameface CDP connection. The module's own
// .d.ts declares it as a flat top-level export instead, which does NOT match runtime: importing
// `{ focusEntity }` directly from "cs2/bindings" resolves to undefined at runtime because
// webpack's `externalsType: "window"` reads window["cs2/bindings"].focusEntity, which doesn't
// exist - the real property is window["cs2/bindings"].camera.focusEntity. Reaching in dynamically
// here (rather than a typed import) avoids relying on that mismatched type declaration.
export function focusEntityOnMap(entity: Entity): void {
    const bindingsModule = (window as any)["cs2/bindings"];
    bindingsModule?.camera?.focusEntity?.(entity);
}
