import { useEffect, useState } from "react";
import { bindValue, trigger, useValue } from "cs2/api";
import { Panel, PanelSection, PanelSectionRow, Portal, Scrollable } from "cs2/ui";
import { DistrictInfo } from "./district-manager-types";
import { focusEntityOnMap } from "./district-manager-navigation";
import styles from "./district-manager-panel.module.scss";
import refreshIcon from "./icons/refresh.svg";
import mapPinIcon from "./icons/map-pin.svg";

// Must match DistrictOverviewUISystem.kGroup on the C# side.
const GROUP = "districtManager";
const DISTRICTS_POLL_MS = 2000;

const panelOpen$ = bindValue<boolean>(GROUP, "panelOpen", false);
const enabled$ = bindValue<boolean>(GROUP, "enabled", true);

// polling instead of the usual useValue(bindValue(...)) pattern - the subscription sometimes
// missed its first update right after a city loads and got stuck on "no districts". A fresh
// bindValue() read each tick doesn't have that problem.
function readDistrictsOnce(): DistrictInfo[] {
    const binding = bindValue<DistrictInfo[]>(GROUP, "districts", []);
    const value = binding.value;
    binding.dispose();
    return value;
}

function happinessClassName(label: string): string {
    switch (label) {
        case "Great":
            return styles.happinessGreat;
        case "Good":
            return styles.happinessGood;
        case "Average":
            return styles.happinessAverage;
        case "Bad":
            return styles.happinessBad;
        case "Terrible":
            return styles.happinessTerrible;
        case "No residents":
            return styles.happinessNone;
        default:
            return styles.happinessTerrible;
    }
}

interface ChipItem {
    key: string | number;
    label: string;
    onClick?: () => void;
}

interface ChipListRowProps {
    label: string;
    items: ChipItem[];
    emptyText: string;
}

// wraps long lists as individual chips instead of one joined string that overflows the panel
// (flex items don't wrap by default in this engine). clickable ones get a hover highlight.
const ChipListRow = ({ label, items, emptyText }: ChipListRowProps) => (
    <div className={styles.chipListRow}>
        <div className={styles.chipListLabel}>{label}</div>
        <div className={styles.chipListValues}>
            {items.length > 0 ? (
                items.map((item) => (
                    <span
                        key={item.key}
                        className={`${styles.chip} ${item.onClick ? styles.chipClickable : ""}`}
                        onClick={item.onClick}
                        title={item.onClick ? "Show on map" : undefined}
                    >
                        {item.label}
                    </span>
                ))
            ) : (
                <span className={styles.chipListEmpty}>{emptyText}</span>
            )}
        </div>
    </div>
);

interface DistrictRowProps {
    district: DistrictInfo;
    expanded: boolean;
    onToggle: () => void;
}

// starts collapsed - click the header to expand
const DistrictRow = ({ district, expanded, onToggle }: DistrictRowProps) => {
    const activePolicyNames = district.policies.map((p) => p.name);

    return (
        <PanelSection>
            <div className={styles.districtHeader} onClick={onToggle}>
                <PanelSectionRow
                    left={
                        <span className={styles.headerLeft}>
                            {district.name}
                            <span className={`${styles.chevron} ${expanded ? styles.chevronExpanded : ""}`}>
                                &#9660;
                            </span>
                            <span
                                className={styles.navigatePin}
                                title="Show on map"
                                onClick={(e) => {
                                    e.stopPropagation();
                                    focusEntityOnMap(district.entity);
                                }}
                            >
                                <img src={mapPinIcon} className={styles.navigatePinIcon} />
                            </span>
                        </span>
                    }
                    right={
                        <span className={happinessClassName(district.happinessLabel)}>
                            {district.happinessLabel}
                            {district.population > 0 ? ` (${district.averageHappiness})` : ""}
                        </span>
                    }
                />
            </div>
            {expanded && (
                <>
                    <PanelSectionRow left="Population" right={district.population} />
                    <ChipListRow
                        label="Policies"
                        items={activePolicyNames.map((name) => ({ key: name, label: name }))}
                        emptyText="None active"
                    />
                    <ChipListRow
                        label="Services"
                        items={district.services.map((service) => ({
                            key: service.entity.index,
                            label: service.name,
                            onClick: () => focusEntityOnMap(service.entity),
                        }))}
                        emptyText="None"
                    />
                    <ChipListRow
                        label="Assets"
                        items={district.assets.map((asset) => ({
                            key: asset.entity.index,
                            label: asset.name,
                            onClick: () => focusEntityOnMap(asset.entity),
                        }))}
                        emptyText="None"
                    />
                    {district.complaints.length > 0 && (
                        <PanelSectionRow
                            left="Complaints"
                            right={<span className={styles.complaint}>{district.complaints.join(", ")}</span>}
                        />
                    )}
                </>
            )}
        </PanelSection>
    );
};

export const DistrictManagerPanel = () => {
    const panelOpen = useValue(panelOpen$);
    const enabled = useValue(enabled$);

    const [rawDistricts, setRawDistricts] = useState<DistrictInfo[]>(() => readDistrictsOnce());
    const [isRefreshing, setIsRefreshing] = useState(false);

    // keyed by district entity index, absent/false = collapsed
    const [expanded, setExpanded] = useState<Record<number, boolean>>({});

    useEffect(() => {
        if (!panelOpen) {
            return;
        }
        setRawDistricts(readDistrictsOnce());
        const intervalId = setInterval(() => setRawDistricts(readDistrictsOnce()), DISTRICTS_POLL_MS);
        return () => clearInterval(intervalId);
    }, [panelOpen]);

    const districts = [...rawDistricts].sort((a, b) => a.name.localeCompare(b.name));

    if (!panelOpen || !enabled) {
        return null;
    }

    const close = () => trigger(GROUP, "togglePanel");
    const refresh = () => {
        if (isRefreshing) {
            return;
        }
        setIsRefreshing(true);
        trigger(GROUP, "refresh");
        setTimeout(() => setRawDistricts(readDistrictsOnce()), 150); // give the C# recompute a moment to land
        setTimeout(() => setIsRefreshing(false), 800); // hold the spinner a bit so it's visible
    };

    const toggleOne = (index: number) => {
        setExpanded((prev) => ({ ...prev, [index]: !prev[index] }));
    };

    const expandAll = () => {
        const all: Record<number, boolean> = {};
        for (const district of districts) {
            all[district.entity.index] = true;
        }
        setExpanded(all);
    };

    const collapseAll = () => setExpanded({});

    return (
        <Portal>
            {/* dimmed backdrop so this reads as a modal, not a docked tray */}
            <div className={styles.backdrop} onClick={close}>
                <Panel
                    className={styles.modal}
                    header={<div>District Manager</div>}
                    onClose={close}
                    onClick={(e) => e.stopPropagation()}
                >
                    <div className={styles.toolbarRow}>
                        {districts.length > 0 && (
                            <div className={styles.expandCollapseRow}>
                                <span className={styles.expandCollapseLink} onClick={expandAll}>
                                    <span className={styles.checkbox} />
                                    Expand all
                                </span>
                                <span className={styles.expandCollapseLink} onClick={collapseAll}>
                                    <span className={styles.checkbox} />
                                    Collapse all
                                </span>
                            </div>
                        )}
                        <span
                            className={`${styles.refreshButton} ${isRefreshing ? styles.refreshButtonActive : ""}`}
                            onClick={() => refresh()}
                            title="Refresh now"
                        >
                            <img
                                src={refreshIcon}
                                className={`${styles.refreshIcon} ${isRefreshing ? styles.refreshIconSpinning : ""}`}
                            />
                        </span>
                    </div>
                    <Scrollable vertical={true} className={styles.scrollable}>
                        {districts.length === 0 ? (
                            <div className={styles.emptyState}>No districts found in this city yet.</div>
                        ) : (
                            districts.map((district) => (
                                <DistrictRow
                                    key={district.entity.index}
                                    district={district}
                                    expanded={!!expanded[district.entity.index]}
                                    onToggle={() => toggleOne(district.entity.index)}
                                />
                            ))
                        )}
                    </Scrollable>
                </Panel>
            </div>
        </Portal>
    );
};
