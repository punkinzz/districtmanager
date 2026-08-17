import { bindValue, trigger, useValue } from "cs2/api";
import { FloatingButton } from "cs2/ui";
import mapPinIcon from "./icons/map-pin.svg";

const GROUP = "districtManager";

const panelOpen$ = bindValue<boolean>(GROUP, "panelOpen", false);
const enabled$ = bindValue<boolean>(GROUP, "enabled", true);

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
