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

// Districts are pulled by polling rather than the usual useValue(bindValue(...)) subscription
// pattern used for panelOpen/enabled above. Reported symptom: right after loading a city, the
// panel would show "no districts" indefinitely, but a single fresh bindValue().value read (done
// manually, outside the component) always returned the real current data, and a full page reload
// always fixed it going forward. That points to the module-level subscription occasionally
// missing its first real update - plausibly because this UI bundle can finish evaluating before
// DistrictOverviewUISystem.OnCreate has registered the "districts" binding on the C# side, right
// as a city is loading. Polling a *fresh* bindValue() call each tick sidesteps that: each read
// gets the real current value directly rather than depending on a subscription's update history,
// so it can't get stuck the same way. See NOTES.md.
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

// Renders a label + a wrapping row of "chips" instead of one long joined string in a single
// flex row - a district with many services/policies was overflowing the panel width and
// breaking the layout, since this engine's flex items don't shrink or wrap by default (no `gap`
// either, so chip spacing is margin-based like everywhere else in this file). Chips with an
// onClick (services, navigable to their building) get a pointer cursor and hover highlight;
// plain ones (policies, nothing to navigate to) don't.
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

// Each district starts collapsed, showing just its name and happiness. Clicking the header
// (or its chevron) expands it to reveal population/policies/services/complaints.
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
                            {district.happinessLabel} ({district.averageHappiness})
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

// Floating panel appended to the "Game" hook target (see index.tsx). Only actually mounts its
// contents while the toolbar button has it open - the binding refresh on the C# side is also
// gated on this same open state, so there's no cost while closed.
export const DistrictManagerPanel = () => {
    const panelOpen = useValue(panelOpen$);
    const enabled = useValue(enabled$);

    const [rawDistricts, setRawDistricts] = useState<DistrictInfo[]>(() => readDistrictsOnce());
    const [isRefreshing, setIsRefreshing] = useState(false);

    // Keyed by district entity index. Absent (or false) = collapsed - every district starts
    // collapsed, matching the "minimized by default" request.
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
        // Give the C# recompute a moment to land, then pull it immediately rather than waiting
        // for the next DISTRICTS_POLL_MS tick.
        setTimeout(() => setRawDistricts(readDistrictsOnce()), 150);
        // The actual refresh is fast enough (150ms) that the indicator would barely be visible -
        // hold it a bit longer than that so clicking it actually reads as having done something.
        setTimeout(() => setIsRefreshing(false), 800);
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
            {/* Dimmed full-screen backdrop makes this read as a modal popup rather than a
                docked tray - clicking outside the panel closes it, clicking inside doesn't. */}
            <div className={styles.backdrop} onClick={close}>
                <Panel
                    className={styles.modal}
                    header={<div>District Manager</div>}
                    onClose={close}
                    onClick={(e) => e.stopPropagation()}
                >
                    {/* The refresh button lives here rather than in the `header` prop above.
                        Originally moved it after a test seemed to show onClick doesn't fire for
                        content passed through `header` - that test turned out to be a false
                        negative (see NOTES.md, "Correction to the refresh button diagnosis"), so
                        `header` may well have worked fine too. Left it here since it works and
                        moving it back has no upside. */}
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
