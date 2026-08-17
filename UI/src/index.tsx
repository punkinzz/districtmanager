import { ModRegistrar } from "cs2/modding";
import { DistrictManagerButton } from "mods/district-manager-button";
import { DistrictManagerPanel } from "mods/district-manager-panel";

const register: ModRegistrar = (moduleRegistry) => {
    moduleRegistry.append("GameTopLeft", DistrictManagerButton);
    moduleRegistry.append("Game", DistrictManagerPanel);
};

export default register;
