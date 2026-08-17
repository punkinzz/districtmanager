import { bindValue, trigger, useValue } from "cs2/api";
import { FloatingButton } from "cs2/ui";
import mapPinIcon from "./icons/map-pin.svg";

// Must match DistrictOverviewUISystem.kGroup on the C# side.
const GROUP = "districtManager";

const panelOpen$ = bindValue<boolean>(GROUP, "panelOpen", false);
const enabled$ = bindValue<boolean>(GROUP, "enabled", true);

// Toolbar icon button appended to GameTopLeft (see index.tsx) that toggles the district panel.
// Hidden entirely when the mod's Options > Enable District Manager toggle is off.
export const DistrictManagerButton = () => {
    const panelOpen = useValue(panelOpen$);
    const enabled = useValue(enabled$);

    if (!enabled) {
        return null;
    }

    return (
        <FloatingButton
            src={mapPinIcon}
            tinted={true}
            selected={panelOpen}
            tooltipLabel="District Manager"
            onSelect={() => trigger(GROUP, "togglePanel")}
        />
    );
};
