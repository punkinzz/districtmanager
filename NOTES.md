# District Manager — Research Notes

Research gathered while waiting on the CS2 install + Visual Studio setup. This file will be extended once the game is installed and we can confirm real ECS type names with the ECS Explorer tool (see "Still to confirm" at the bottom).

## District policies in Cities: Skylines II

Seven district-level policies exist in the base game (source: [GameSkinny guide](https://www.gameskinny.com/tips/cities-skylines-2-how-to-set-district-and-city-policies/)):

| Policy | Effect | Unlock |
|---|---|---|
| Energy Consumption Awareness | Electricity use reduced by 5% | — |
| Recycling | Reduces resources used, lowers citizen free time | — |
| Roadside Parking Fee | Increases city revenue (adjustable fee) | — |
| Speed Bumps | Lowers accident rate and noise pollution | — |
| Heavy Traffic Ban | Reduces noise pollution, helps traffic flow | Milestone 6 |
| Gated Community | Increases well-being, reduces crime | Milestone 7 |
| Combustion Engine Ban | Reduces noise and air pollution | Milestone 10 |

At least one (Roadside Parking Fee) has an adjustable intensity/fee parameter — worth showing that value, not just on/off, in the panel. Need to confirm in-game whether others also have sliders.

The game's own District panel already shows population, average wealth, education level, happiness (with a pros/cons breakdown on hover), and active policies per district — so a lot of the aggregation logic we need already exists somewhere in the game's own systems. Worth checking during ECS discovery whether we can read from/alongside that existing system rather than re-deriving happiness from raw citizen data ourselves.

## Reference mod architectures

Two different toolchains exist in the wild — important not to mix patterns:

**1. Official toolchain (what we're using)** — C# project using `Colossal.UI.Binding` + `UISystemBase`, built via the in-game-installed VS template. Best real-world example found: [toverux/HallOfFame](https://github.com/toverux/HallOfFame), fully open source, project layout:
- `HallOfFame/` — C# project root (`Mod.cs`, `Settings.cs`, `HallOfFame.csproj`)
  - `Systems/` — `UISystemBase` subclasses (e.g. `CommonUISystem.cs`)
  - `UI/` — TypeScript/React source, built automatically alongside the C# build
  - `Domain/`, `Http/`, `Services/`, `Utils/`, etc. — supporting code
- `HallOfFame.Tests/` — unit tests
- `HallOfFame.sln` — solution tying it together

Confirmed binding pattern from `CommonUISystem.cs` (matches the official UI Modding wiki):
```csharp
internal sealed partial class CommonUISystem : UISystemBase
{
    private const string BindingGroup = "hallOfFame.common";
    // AddBinding(new GetterValueBinding<T>(BindingGroup, "name", () => value));
    // AddBinding(new TriggerBinding<string>(BindingGroup, "name", HandlerMethod));
}
```
When a binding only needs to change in response to specific events (not every frame), the system disables its own `OnUpdate` (`Enabled = false` in `OnCreate`) and instead calls the binding's update method manually from event handlers. **For our mod we do need a periodic refresh** (district stats change continuously), so we'll keep `OnUpdate` active but throttle it (e.g. only recompute every N frames or every ~1–2s of sim time) rather than copying HallOfFame's event-driven-only approach verbatim.

**2. Legacy/community toolchain (NOT what we're using)** — BepInEx 6 + HookUI, where the UI is a transpiled JS file dropped into `Cities2_Data/StreamingAssets/~UI~/HookUI/Extensions`. Examples: [Captain-Of-Coit/cs2-city-monitor](https://github.com/Captain-Of-Coit/cs2-city-monitor) ("City Vitals" for CS2 — tracks ~18 city-wide vitals: electricity/water/sewage/landfill availability, healthcare, crime, education availability, etc.) and [Cities2Modding/UltimateMonitor](https://github.com/Cities2Modding/UltimateMonitor) (unemployment monitor, forked from CityMonitor). Useful for understanding *what* data the game tracks and roughly where (e.g. `electricityInfo.electricityAvailability`-style access), but their plumbing is the pre-official-toolchain route and not a pattern to copy structurally.

## Confirmed real ECS/API types (decompiled directly from the installed `Game.dll`)

The game and Visual Studio are now both installed, and the `csiimod` dotnet template is registered (`dotnet new list` shows "Cities Skylines II mod"). Rather than waiting on the browser-based ECS Explorer, decompiled the actual `Cities2_Data\Managed\Game.dll` with `ilspycmd` (installed as a pinned-version dotnet global tool: `dotnet tool install -g ilspycmd --version 8.2.0.7535`, run with `DOTNET_ROLL_FORWARD=LatestMajor` since it targets net6.0 and only net8/9 are installed). This gives verified, real signatures instead of guesses:

**District entity**
```csharp
// Game.Areas.District — real district marker component
public struct District : IComponentData, IQueryTypeParameter, ISerializable
{
    public uint m_OptionMask;
}
```
Query for all real (non-preview) districts, straight from the vanilla `DistrictsSection`:
```csharp
GetEntityQuery(ComponentType.ReadOnly<District>(), ComponentType.Exclude<Temp>());
```
Buildings/citizens carry `Game.Areas.CurrentDistrict` (`.m_District` = the owning district's `Entity`) — this is how membership in a district is determined.

**Happiness** — citizens have a cached `Happiness` field directly on `Game.Citizens.Citizen` (no need to recompute it): `citizenFromEntity[citizen].Happiness`. The vanilla `AverageHappinessSection` (powers the panel when you select a building/household/district) averages this across all citizens in a district's residential buildings, found via a job matching `CurrentDistrict.m_District == selectedDistrict`. `CitizenUIUtils.GetCitizenHappiness(avg)` converts the average int into the `Game.UI.InGame.CitizenHappiness` UI struct (a happiness "bucket", used for the smiley/color tier).

**Complaints = negative happiness factors.** The game has no "complaint" type as such — what reads as a complaint is a negative-weighted entry in `Game.Simulation.CitizenHappinessSystem.HappinessFactor`:
```csharp
public enum HappinessFactor
{
    Telecom, Crime, AirPollution, Apartment, Electricity, Healthcare, GroundPollution,
    NoisePollution, Water, WaterPollution, Sewage, Garbage, Entertainment, Education,
    Mail, Welfare, Leisure, Tax, Buildings, Consumption, TrafficPenalty, DeathPenalty,
    Homelessness, ElectricityFee, WaterFee, Unemployment, Count
}
```
(there's a building-scoped sibling enum too, `Game.Buildings.BuildingHappinessFactor`, used for a single building rather than a district-wide aggregate). Factor breakdowns are carried as `Game.UI.InGame.FactorInfo { int factor; int weight; }` — `factor` is the enum index, `weight` the (signed) magnitude; sort by `Math.Abs(weight)` descending and take the top N with negative weight as "top complaints".

**Services** — `Game.Net.CoverageService` is the real enum of service categories used for district/building coverage:
```csharp
public enum CoverageService : byte { Healthcare, FireRescue, Police, Park, PostService, Education, EmergencyShelter, Welfare, Count }
```
carried per-network-object via `Game.Net.CoverageServiceType { CoverageService m_Service }` (a shared component) and `Game.Net.ServiceCoverage : IBufferElementData { float2 m_Coverage }` (buffer, coverage extents). Note this enum is narrower than city-wide `CityService`/`CityServiceUpkeep` (which include things like garbage/electricity/water handled differently) — worth deciding in implementation whether "services" in our panel means this per-area `CoverageService` set, or a broader union.

**Policies** — confirmed via `Game.Policies.Policy` (the buffer element on any entity, including districts, that can carry policies):
```csharp
public struct Policy : IBufferElementData, ISerializable
{
    public Entity m_Policy;       // reference to the PolicyPrefab entity
    public PolicyFlags m_Flags;   // includes an Active bit
    public float m_Adjustment;    // slider/intensity value, e.g. the parking fee amount
}
```
The vanilla `Game.UI.InGame.PoliciesUISystem` shows the exact pattern (in `FindAndSortPolicies`/`ExtractInfo`, both private, so not directly callable from our mod — but trivial to mirror):
1. Query policy prefab entities relevant to districts: `GetEntityQuery(new EntityQueryDesc { All = { PolicyData }, Any = { DistrictOptionData, DistrictModifierData } })`.
2. For each district entity, read its `DynamicBuffer<Policy>`.
3. Cross-reference: a policy prefab entity from the query is "active" on that district if it appears in the buffer with `PolicyFlags.Active` set; `m_Adjustment` is its current slider value (compare against `Game.Prefabs.PolicySliderData.m_Default` when no override is present).
4. Get the prefab's display name via `Game.Prefabs.PolicyPrefab.name` + the game's localization dictionary key `Policy.TITLE[{name}]`.

This confirms: our mod does NOT need to touch any private vanilla UI class — everything needed (the `District`/`Policy`/`CurrentDistrict` components, the `HappinessFactor`/`CoverageService` enums, and `Citizen.Happiness`) is public ECS data, queryable from our own `UISystemBase` exactly the way the vanilla sections do it internally.

## Bugs found from first in-game test (2026-08-16)

Tested build showed: panel rendered as a small tray instead of a modal, the mod's Options page showed confusing unrelated demo controls, and the panel always said "no districts found" despite 2 real districts existing. Checked `Logs/DistrictManager.Mod.log`, `Logs/Modding.log`, `Logs/UI.log`, and `Player.log` - the mod loaded cleanly (`Loaded DistrictManager ... in 23.8ms`, UI module registered fine) with **zero errors logged anywhere**, which is itself informative: it means any exception inside `RefreshDistricts()` (which had no try/catch at all) was being silently swallowed by the trigger-binding bridge rather than surfacing anywhere - a single bad line could wipe the whole districts list with no trace.

Fixes applied:
1. **Modal overlay** - `district-manager-panel.tsx` now wraps `Panel` in a full-screen dimmed backdrop (`.backdrop`/`.modal` in the scss module) instead of relying on `Panel`/`Portal` alone, which rendered as a small docked tray.
2. **Confusing Options page** - `Setting.cs` was still the template's demo Button/Toggle/Slider/Dropdown scaffolding, unrelated to this mod. Stripped to just the mod name (no options exposed yet). While in there, found and fixed a real bug: the template's `SetDefaults()` threw `NotImplementedException` unconditionally - called from `AssetDatabase.global.LoadSettings(...)` in `Mod.OnLoad()`. Since `Mod.log.SetShowsErrorsInUI(false)`, this would fail with zero visible sign to the player.
3. **"No districts found"** - the most likely real cause: `GatherDistrictPolicies` called `PrefabSystem.TryGetPrefab<PolicyPrefab>(...)` where `PolicyPrefab` is an **abstract class** - untested territory beyond "it compiled." Replaced with a plain ECS component read (`EntityManager.TryGetComponent<PolicyData>` + `PrefabSystem.GetPrefabName(Entity)`, both concrete/confirmed-safe patterns from vanilla `NameSystem`), and wrapped the whole refresh in try/catch with `Mod.log.Error(...)` so any future failure actually shows up in `Logs/DistrictManager.Mod.log` instead of vanishing.

**Still to confirm**: whether this actually fixes the empty-districts symptom, since nothing in the logs directly proved the abstract-prefab call was the culprit (no exception trace existed anywhere pre-fix). If it's still broken after this, the new logging should finally show why.

## Second round - confirmed live via Gameface DevTools MCP (2026-08-16)

Installed the `coherent-gameface` Claude Code plugin (`CitiesSkylinesModding/agents-plugins`), which drives the game's Coherent Gameface UI directly over CDP. Requires launching the game with **both** `-developerMode` and `-uiDeveloperMode` (only `-developerMode` opens the in-game Home+Tab debug menu; `-uiDeveloperMode` is the one that actually opens the CDP port at `localhost:9444` - found in `plugins/cs2-modding/skills/cs2-modding-setup/SKILL.md` in that repo, not documented anywhere in the general Gameface docs).

With a live connection, inspected the actual DOM/CSS instead of guessing further:

- **Modal was tiny/docked, not centered**: `.backdrop { position: fixed; inset: 0; ... }` rendered as a ~50x115px box instead of full-screen. Root cause confirmed via a style round-trip probe (`el.style.setProperty('inset', '0')` then read back): **`inset` is not supported by this Cohtml version (1.64.0.7) - it silently no-ops**, leaving `position: fixed` with no `top`/`right`/`bottom`/`left` to anchor to. Individual `top`/`right`/`bottom`/`left` properties DO work. Fixed by replacing the `inset: 0` shorthand with explicit longhand properties.
- **Everything was tiny even after that fix**: this game's root font-size is `0.0520833vw`, which resolves to exactly **1px at the 1920px reference resolution** - i.e. `1rem ≈ 1px` in this UI, not the browser-standard `1rem = 16px`. Every rem-based size in `district-manager-panel.module.scss` was off by roughly 16x as a result (a "50rem" modal was 50px, not ~800px). Rescaled all sizes accordingly (e.g. modal width `50rem` -> `800rem`).
- **"No districts found"**: turned out to already be fixed by the earlier `GatherDistrictPolicies` rewrite - confirmed directly by reading the live value binding via `game_eval` (`window["cs2/api"].bindValue("districtManager", "districts", ...).value`), which returned 3 fully-populated real districts (Linden Way, Cooper Glen, Aspen Bluff). The DOM (`game_dom`) also already contained all 3 rendered correctly. What the user was seeing was almost certainly stale state from testing before the fixed DLL had been loaded (C# mods only reload on a full game restart, unlike the UI bundle which can be hot-reloaded via `location.reload()` over the CDP connection).

After rebuilding with the CSS fixes and live-reloading the UI (`location.reload()` + `game_wait({reload:true, ...})` - no full game restart needed), verified end-to-end via an actual `game_click` on the toolbar button (not just direct binding inspection): opens a properly centered, dimmed modal listing all 3 districts with correct data, closes via the X button. Zero console errors throughout.

## Third round (2026-08-16, later)

**Services bug found by user testing**: a new district ("Evergreen Terrace") with a dedicated police station didn't show it under Services. Root cause: the original services query only picked up buildings with a populated `ServiceDistrict` buffer - i.e. buildings manually restricted to a district via the in-game "assign district to this service building" tool, which almost nobody uses. Replaced with `Game.City.CityServiceUpkeep` (confirmed via decompile: an empty tag `IComponentData` on every city service building regardless of category) combined with the same `CurrentDistrict` link already used for population - i.e. "services located in this district" instead of "services explicitly restricted to this district". Much simpler query too, reusing the exact same building-district association. **Not yet deployed** - the game had the old DLL locked (`MSB3231: Access to the path ... is denied` on the deploy step) so this needs a full game restart to test, unlike the UI-only fixes which hot-reload live.

**Enable/disable Options toggle added**: the Options page previously showed nothing (I'd stripped the template's confusing demo controls down to just the mod name). Added a real `Enabled` toggle. Confirmed via decompile that `Game.Settings.Setting` (base of `Game.Modding.ModSetting`) exposes `public event OnSettingsAppliedHandler onSettingsApplied` and `Apply()` invokes it - the standard way to react to an Options-page change. Wired: `Mod.Instance` (static) exposes the `Setting`, `DistrictOverviewUISystem` subscribes lazily in `OnUpdate` (ECS auto-creates systems independently of `Mod.OnLoad`'s ordering, so `Mod.Instance` may not exist yet when this system's own `OnCreate` runs), pushes an `enabled` value binding, and force-closes the panel if disabled mid-session. TS side hides the toolbar button entirely and the panel when `enabled` is false. Also blocked on the same DLL lock - needs a restart to verify.

**Collapsible districts + Expand/Collapse all added** (fully live-tested, no restart needed): each district row starts collapsed (name + happiness only), with a chevron toggle; added "Expand all"/"Collapse all" controls with small checkbox glyphs (☐) rather than real `<input type="checkbox">` elements, since native checkboxes need the game's own form polyfill which isn't guaranteed available to an arbitrary mod-injected panel. Chevron sits after the district name per user request.

**Second engine quirk found**: flexbox `gap` is *also* unsupported by this Cohtml version (same "silently rejected, round-trips to empty string" signature as `inset` earlier). Used explicit `margin-right`/`margin-left` on the relevant child elements instead of `gap` on the flex container. Worth checking for anywhere else `gap` might get reached for in future work on this UI.

## Fourth round (2026-08-16, later still)

- **Checkbox glyph didn't scale**: the ☐ (U+2610) unicode character had no glyph in this game's font, so it silently fell back to a tiny placeholder box no matter what `font-size` was set - increasing font-size did nothing because there was no real character being drawn. Replaced with a plain CSS-drawn box (`width`/`height`/`border` on an empty `span`) instead of relying on font glyph coverage. Worth remembering for any future icon-as-text-glyph idea in this UI - verify the glyph actually renders, not just that the DOM has the right character.
- **Third engine quirk found**: `currentColor` doesn't exist in this Cohtml version (matches the `gameface` skill's documented gap - confirmed by cross-referencing rather than testing live, since a wrong border color is low-risk/easy to eyeball-verify either way). Used an explicit hex color for the checkbox border instead.
- **`getComputedStyle` doesn't resolve units** in this engine - it echoes back the literal specified value (e.g. a `width: 800rem` rule reads back via `getComputedStyle().width` as the string `"800rem"`, not a resolved pixel value), even though the rem value *is* correctly applied visually (confirmed: the modal really does render at ~800px). Don't use `getComputedStyle` output as proof a size is "wrong" - screenshot/measure the actual rendered rect instead.
- Added a manual refresh icon button (circular-arrow SVG, bundled the same way as the map-pin toolbar icon) in the panel's title bar, wired to a new `refresh` trigger binding on the C# side - lets the player force an update without waiting for the ~2s auto-refresh tick. Not yet deployed (same DLL lock as the services/enabled-toggle fixes above).
- Complaints now show only the single highest-severity issue instead of every applicable one, per user request. Ranked via a `BuildTopComplaint` helper that scores each candidate on a rough comparable scale (happiness: points below threshold; crime/garbage: % above city average; "no services": a fixed documented baseline of 20) and keeps just the top one. Not yet deployed (same DLL lock).
- Districts are now sorted alphabetically by name for display - done client-side in the TS panel component (`[...rawDistricts].sort((a, b) => a.name.localeCompare(b.name))`), not on the C# side, since it's a pure display concern.

## Fifth round (2026-08-16, later still) - map-pin navigation

Added a per-district "show on map" pin icon next to the chevron, using the game's own camera-focus mechanism rather than reinventing one.

**Real runtime/types mismatch found in `cs2/bindings`**: its `.d.ts` declares `focusEntity`/`focusedEntity$` as flat top-level exports, but the actual runtime object (`window["cs2/bindings"]`) nests them under a `camera` sub-object (`window["cs2/bindings"].camera.focusEntity`). Confirmed live via `game_eval` (`Object.keys(window["cs2/bindings"])` listed `camera`, `map`, `selectedInfo`, etc. as the top-level keys, each holding its own functions/bindings - not a flat namespace like the types imply). Since webpack's `externalsType: "window"` for `cs2/bindings` reads properties directly off `window["cs2/bindings"]`, a typed `import { focusEntity } from "cs2/bindings"` would silently resolve to `undefined` at runtime and throw when called - the type declarations for this module can't be trusted for shape, only argument/return types once you've found the real path. Worked around by reaching in dynamically (`(window as any)["cs2/bindings"].camera.focusEntity`) in a small `district-manager-navigation.ts` helper instead of a typed import - documented inline so it's not "fixed" back to a typed import later without knowing why.

Verified the actual call works correctly (not just that it didn't throw): called `camera.focusEntity(entity)` for one district, then in a **separate** `game_eval` call after a `game_wait`, confirmed `camera.focusedEntity$.value` updated to that exact entity - a synchronous same-call check showed stale data, since the update round-trips through the game's own frame loop and isn't visible until at least the next frame.

Also reused the toolbar's `map-pin.svg` for this - but that one only ever worked because the toolbar button renders it via CSS `mask-image` (ignores the source file's own colors entirely, using only its alpha/shape), whereas this new pin renders as a plain `<img>`, where the file's original `fill="currentColor"` would matter and does NOT resolve in this engine. Changed the shared icon file to an explicit hex fill - harmless for the mask-based toolbar usage, necessary for this one.

## Sixth round - deploy corruption (twice), stale-save data, and a real crash

**Deploy corruption, self-inflicted, twice.** The C# project's MSBuild deploy step wipes the *entire* `Mods/DistrictManager` folder before copying its own output. Running `dotnet build` "just to check compilation" while the game was running failed on the file lock (as expected) but had ALREADY deleted files before hitting the lock, each time - across a few such attempts this silently stripped `DistrictManager.dll` down to nothing while the game kept running, then a *second* time it wiped the just-rebuilt UI bundle when I rebuilt the C# side without immediately following with a UI rebuild. Lesson, now firmly learned: never run `dotnet build` while the game is running (even to "just check" compilation - it still executes the destructive deploy step and fails at the copy, not before it), and always rebuild+redeploy **both** halves together, C# then UI, never one without the other.

**Stale districts across a save load, confirmed and fixed.** Loading a different save left the panel showing the previous save's districts. Root cause (same pattern as vanilla `PoliciesUISystem`, which exists specifically to solve this): our system never cleared `m_Districts` on a new game/save load. Added an `OnGameLoaded(Context)` override that clears the cache and re-refreshes if the panel's open - mirrors the exact vanilla pattern found earlier via decompile, not a new invention.

**Refresh button silently did nothing - real bug, found and fixed.** Confirmed live with a proper test rather than assumption: installed a spy on `cs2/api.trigger` and a raw native `addEventListener` on the actual button element, then dispatched a real `game_click`. The raw listener fired; React's `onClick` never did. Root cause: the button lived inside `Panel`'s `header` prop, and content passed through that prop does not appear to get React's synthetic click events in this engine (rendered outside the normal event-delegation path somehow). Fixed by moving the refresh button out of `header` into the regular content area, next to Expand/Collapse all, where every other working click handler in this panel already lives. **Lesson: don't put interactive elements inside `Panel`'s `header` prop in this engine - content there doesn't get real click events.**

**Services/policies list overflow, fixed.** A district with many services rendered them as one long joined string in a single flex row, which overflowed the panel width instead of wrapping (this engine's flex items don't shrink or wrap by default). Replaced with a `ChipListRow` component: label + a `flex-wrap: wrap` container of individual chips, spaced with margin (not `gap`, still unsupported). Applied to both Services and Policies.

**Correction to the "refresh button doesn't work" diagnosis above.** That diagnosis was wrong, and it's worth recording *why*, since the flawed test looked completely convincing at the time. I "confirmed" it via a spy: `window["cs2/api"].trigger = wrappedFn`, then checked whether a real `game_click` recorded a call. It never did - across several rebuilds, moving the button out of `header`, static vs dynamic classNames, named vs inline handlers, nothing changed the result. The spy was the problem: this file does `import { trigger } from "cs2/api"`, and with webpack's `externalsType: "window"` that almost certainly gets bound to the *original* function reference at module-evaluation time, not a live property lookup - so reassigning `window["cs2/api"].trigger` later is invisible to code that already imported it. The spy could only ever show `[]`, regardless of whether the click worked. Caught it by adding a plain `console.log` at the top of `refresh()` instead (no import, no interception, just a raw native call) - it logged immediately on click, proving the handler fires fine. **Lesson: don't spy on a `cs2/api` (or any externals-imported) function by reassigning the module property - verify with a direct, import-free side effect instead** (a `console.log`, a DOM/class check, or a state-driven visual change), since the spy pattern gives a confident-looking false negative here.

Net effect on the code: the button ended up moved out of `header` and using an inline arrow handler anyway, which is harmless and arguably clearer, but neither change was actually the fix - the button likely worked in `header` too. Left the "why" in the code comment for future reference so this doesn't get "fixed" again based on the same flawed test.

**A real native crash occurred.** `Player.log`'s "Native Crash Reporting" section shows a fatal error inside Cohtml's own per-frame `View::Advance()` call (`cohtmlNativePINVOKE:View_Advance` -> `UIView:Update` -> `UIManager:Update` -> `GameManager:Update`) - i.e. the native UI rendering engine crashed, not managed code throwing a normal exception. Two real candidate causes, not distinguished yet: (1) this session did 38+ `location.reload()` calls plus a lot of direct `trigger()`/`bindValue()` manipulation via the CDP connection - a much more aggressive usage pattern than real play, which could plausibly destabilize the native view on its own; (2) the services-overflow bug above, if a long enough unwrapped line hit some native layout edge case. No `OnDispose` was logged for that session, consistent with an abrupt/crashed exit rather than a clean one. Next step once relaunched: test more normally (fewer forced reloads) and specifically re-check a district with many services, to see whether the crash was tied to the layout bug (now fixed) or to the heavy CDP-driven testing itself.

## Open implementation decisions for task #5

- "Services" scope: `Game.Net.CoverageService` (Healthcare/FireRescue/Police/Park/PostService/Education/EmergencyShelter/Welfare) vs. also including city-wide utilities (garbage/electricity/water/sewage) that show up as `HappinessFactor` entries but aren't in `CoverageService`. Leaning toward: show `CoverageService` coverage per district as "services", and let utilities/pollution etc. surface naturally as happiness-factor complaints instead of double-counting them as both.
- Confirm at runtime whether policies beyond Roadside Parking Fee have a real (non-default) `PolicySliderData` — the type exists and is wired into `Policy.m_Adjustment`, so likely more than one does.

## Seventh round (2026-08-16, later still) - services scoping fix and new Assets section

**Services still showed non-district-specific things (parks, water towers).** The `CityServiceUpkeep` + `CurrentDistrict` query from the sixth round was closer than the original `ServiceDistrict`-buffer-contents approach, but `CityServiceUpkeep` tags every city service building regardless of category - parks, water towers, and other city-wide infrastructure that can never actually be assigned to a specific district all carried it too. Fixed by adding `ComponentType.ReadOnly<ServiceDistrict>()` back into the query, but as a *presence* filter this time (not reading its contents like the original buggy approach did) - having that buffer component at all, regardless of whether it's been assigned anything, is what marks a building's service type as one the vanilla "assign district to this service building" tool can actually target in the first place. So Services now requires: `Building` + `CurrentDistrict` + `CityServiceUpkeep` + `ServiceDistrict` (buffer present) - excluding `Temp`/`Deleted` as before.

**New "Assets" section added**, per request, for things like parks and signature/landmark buildings physically located in a district - explicitly NOT district-assignable, so they don't belong in Services, but still useful to see per-district. Confirmed via `ilspycmd` decompile that `Game.Buildings.Park` (`IComponentData { short m_Maintenance; }`) and `Game.Buildings.Signature` (empty tag `IComponentData`) are real simple marker components already reachable via the existing `Game.Buildings` using directive - no new namespace needed. New query: `Building` + `CurrentDistrict`, `Any` of [`Park`, `Signature`, `CityServiceUpkeep`], excluding `Temp`/`Deleted`/`ServiceDistrict` - the `ServiceDistrict` exclusion is what keeps Assets and Services from double-counting the same building (some Park/Signature buildings still carry `CityServiceUpkeep`). Reused the existing `ServiceInfo` struct (name + entity) for asset entries rather than a new type, since the shape is identical and it's rendered/navigated the same way (clickable chip -> `focusEntityOnMap`).

**Build gotcha repeated once more, non-destructively this time**: built the UI bundle first, then the C# side - the C# deploy step's folder-wipe-before-copy stripped the just-built UI bundle out again (same class of bug as the sixth round, this time caught immediately by checking `ls` on the deployed folder afterward instead of assuming). Fixed by re-running the UI build a second time, after the C# build, so both land together. Confirmed via `ls -la` with timestamps that all 11 deployed files (6 C# artifacts + `.mjs`/`.css`/`.LICENSE.txt`/2 SVGs) are present and current.

**Env var propagation gotcha**: `CSII_USERDATAPATH`, `CSII_MODSPATH`, `CSII_TOOLPATH`, and `DOTNET_ROLL_FORWARD` are all set at the Windows *User* environment scope, but a shell process already running when they were set doesn't see them - each `dotnet build`/`npm run build` in a fresh tool call needs them re-read explicitly via `[System.Environment]::GetEnvironmentVariable(name, "User")` and assigned into `$env:` for that call, rather than assuming they're already in the ambient environment. Missing `DOTNET_ROLL_FORWARD` specifically manifested as `ModPostProcessor.exe` (a net6.0 tool) failing with exit code `-2147450730` and no other message - same underlying cause as the earlier `ilspycmd` issue, just a different tool hitting it.

## Eighth round (2026-08-16, later still) - the real Services bug

**User caught a concrete case**: Hillside Crest showed "Small Medical Clinic" under Services, but that specific building has no district restriction set at all in-game.

**Root cause finally nailed down via decompile** (`Game.Areas.ServiceDistrict`, not previously decompiled - only assumed): it's `IBufferElementData : IEquatable<ServiceDistrict>, ISerializable { public Entity m_District; }` with `[InternalBufferCapacity(0)]` - a real per-building list of the district(s) that building has been explicitly assigned to serve, via the vanilla "restrict this service building to a district" tool. The seventh round's fix only checked whether the entity's *archetype* declares this buffer component at all (true for every eligible service-building prefab, whether or not any assignment has actually been made) - it never read the buffer's actual contents, and instead derived the district purely from the building's physical `CurrentDistrict`. So an ordinary, never-assigned "Small Medical Clinic" physically sitting inside Hillside Crest's borders still got listed under Hillside Crest's Services, exactly as reported.

**Real fix**: dropped `CurrentDistrict` from the services query entirely (district association no longer comes from physical location) and now read the `ServiceDistrict` buffer's actual entries per building in `RefreshDistrictsInternal` (`EntityManager.TryGetBuffer<ServiceDistrict>`, skip if empty), listing the building under every district its buffer actually references (a building's assignment tool allows assigning it to more than one district, so it's not necessarily just one). A building nobody has ever assigned now correctly shows under no district at all, in neither Services nor Assets (Assets already excluded any building with the `ServiceDistrict` component present at all, regardless of contents, so this required no change on that side).

**Historical note, now resolved**: this same buffer-contents approach was the *original* Services implementation, abandoned in the third round because it appeared to show nothing (misread as "manual assignment, rarely used" -> replaced with location-based `CurrentDistrict`). In hindsight that original approach was the semantically correct one all along; "shows nothing" was very likely just the small test city at the time genuinely having zero manual assignments, not a bug in the approach itself. Lesson: an "it shows nothing" result needs to be checked against known ground truth (did the player actually assign anything?) before concluding the query itself is wrong.

## Ninth round (2026-08-17) - published to Paradox Mods

Published District Manager to Paradox Mods. **Mod Id: 155583** (saved into `Properties/PublishConfiguration.xml`'s `ModId` so any future republish updates this listing instead of creating a duplicate).

**Assets prepared beforehand:**
- Replaced the template's placeholder hammer-icon `Thumbnail.png` with a custom 950x500 image (matches the mod's own map-pin icon and dark/blue color scheme) - built as an HTML/CSS/SVG mockup, rendered via a local Chrome tab (file:// URLs are blocked by the browser extension, so served over a throwaway local Node http server instead), screenshotted at the CDP-reported DPR-scaled region, then downscaled to exactly 950x500 via an in-page `<canvas>` + `toDataURL`, POSTed back to the same local server to avoid piping a huge base64 blob through tool output.
- Captured `Screenshot1.png` as a real, live capture of the panel open in-game (not a mockup) - the Gameface CDP plugin had disconnected by this point, so used a plain Win32 `GetWindowRect`/`CopyFromScreen` capture via PowerShell instead (had to `ShowWindow(hwnd, 9)` first - the game window was minimized, reporting a bogus off-screen rect until restored).
- Wrote real `ShortDescription`/`LongDescription` text and set `GameVersion` to the actual installed version (`1.6.*`, read from `Player.log`'s "Game version:" line - `Cities2.exe`'s own file version resource only reports the Unity engine version, not the game's).

**Publish mechanics, confirmed by actually running it:**
- `dotnet publish DistrictManager.csproj -p:PublishProfile=PublishNewMod` runs a normal `Build` first (Release config per the pubxml), which - same as `dotnet build` - wipes and redeploys the `Mods/DistrictManager` folder via the `DeployWIP` target (`AfterTargets="AfterBuild"`), then invokes `ModPublisher.exe` against whatever's in that folder. Since `DeployWIP` only copies the C# build's own output, letting a plain `dotnet publish` do its own build would upload a mod missing the UI bundle (same class of bug as the recurring build-order gotcha elsewhere in this file). Fixed by building explicitly in the right order first (`dotnet build -c Release`, then `npm run build` in `UI/`, confirming all 11 files present) and then running `dotnet publish ... --no-build` so it skips `Build`/`DeployWIP` entirely and just runs the `Publish` target against the already-correct deploy folder.
- `--no-build` has a side effect: it also skips `BuildGetFullPaths` (`BeforeTargets="BeforeBuild"`), which is what normally computes `$(DeployDir)` - so the publish step's `-c` (content folder) arg came through empty ("Error while processing args: <contentFolder> not set") until `DeployDir` was passed explicitly on the command line (`-p:DeployDir="...\Mods\DistrictManager"`).
- Found (the hard way, via a wrong assumption baked into earlier build commands) that the actual env var behind the local mods deploy path is `CSII_LOCALMODSPATH`, not `CSII_MODSPATH` as had been assumed and set (harmlessly uselessly) in every prior build command in this file - MSBuild reads it directly from the User-scope registry via `[System.Environment]::GetEnvironmentVariable(...)` inside `Mod.props`, completely independent of whatever's in the calling shell's own `$env:` block, which is why builds worked fine despite the wrong variable name being set process-side the whole time.
- `ModPublisher.exe` handles PDX Account auth itself with no interactive prompt needed - logged output showed `Trying to auto log in using in-game login` / `Auto logged in with account "jma*****@gma**.com"`, presumably reusing the same session the in-game Modding toolchain login already established.
- **Tag values are a large fixed enum, not free text** - guessed values ("UI", "Info Panel", "Districts") were all rejected with `Attribute UI is invalid. It should be one of Map, Savegame, Code Mod, Prefab, Themes, ...` (a long list, mostly asset/building/network/service categories aimed at content mods, not really applicable to a pure info-panel code mod). `Code Mod` gets added automatically regardless of what's in `PublishConfiguration.xml`'s `Tag` entries (visible in the publisher's own logged metadata even before any valid custom tag was set). Left `Tag` empty for this release since nothing in the enum fit better than the automatic `Code Mod` tag; the actual Paradox Mods website may still let a proper category be picked at the account/mod-management level after the fact.

**To publish an update in the future**: bump `ModVersion`, fill in `ChangeLog`, rebuild in the same order (Release C# then UI, verify all 11 files), then `dotnet publish DistrictManager.csproj -p:PublishProfile=PublishNewMod -p:DeployDir="<mods path>\DistrictManager" --no-build` again - `ModId` is already set so this updates the existing listing (Id 155583) rather than creating a new one. Alternatively `PublishProfiles/PublishNewVersion.pubxml` / `UpdatePublishedConfiguration.pubxml` exist in the template for this but weren't tried this round.

**Live mod page**: https://mods.paradoxplaza.com/mods/155583/Windows (confirmed by direct navigation - URL pattern is `https://mods.paradoxplaza.com/mods/<ModId>/Windows`).

## Tenth round (2026-08-17) - v1.1 update with expanded/collapsed screenshots

Added two more screenshots (`Screenshot2.jpg` = all districts expanded, `Screenshot3.jpg` = all collapsed) and republished as version 1.1.

**Captured via real mouse clicks on the live game window**, not the CDP dev-tools plugin (it had disconnected earlier this session and never came back). Used Win32 `ShowWindow`/`SetForegroundWindow`/`SetCursorPos`/`mouse_event` from PowerShell to restore, focus, and click. Two things learned the hard way:
- Restore+foreground and the click must happen in the *same* script invocation - doing them as separate PowerShell calls let the game's exclusive-fullscreen window re-minimize in between (it auto-minimizes on any foreground loss), so a click sent in a later call landed on the desktop instead and did nothing (confirmed via `IsIconic`/`GetWindowRect` going back to the `-25600,-25600` minimized sentinel position).
- Even with focus timing fixed, synthetic `mouse_event` clicks didn't reliably register with the Gameface UI layer itself (game kept simulating/rendering fine, proving the window had focus, but "Collapse all" visibly did nothing) - unlike the CDP-driven `game_click` used earlier in this project, which dispatches through the actual UI event pipeline. Coherent Gameface apparently doesn't respond to raw OS-level input injection the same way. Asked the user to click "Collapse all" manually instead, then captured the result - reliable, no further automation attempts needed.

**Publish command gotcha**: retried the exact same `dotnet publish ... -p:PublishProfile=PublishNewMod` command used for the first release, and it correctly refused ("ModId must be not set or be equal to 0 (zero) in configuration to publish a new mod") - `PublishNewMod` is only for first-time publishes. Updating an existing mod needs `Properties/PublishProfiles/PublishNewVersion.pubxml` instead (`ModPublisherCommand=NewVersion`), which the template already provides. Same `--no-build -p:DeployDir=...` pattern applies (see ninth round notes above) since no C# was rebuilt for this content-only update.

**Image size limit, found by hitting it**: Paradox Mods rejects any thumbnail/screenshot over 2.1 MB ("Could not publish new mod version: Image size should not exceed 2.1 MB"). The two new screenshots (raw `CopyFromScreen` PNGs) were 2.28 MB and 2.66 MB - both over. Re-encoded as JPEG (quality 85) via `System.Drawing`, which dropped them to ~300 KB and ~265 KB with no visible quality loss on this kind of content (UI text stayed crisp), well under the limit. `Screenshot1.png` from the original release happened to be under the limit already (1.74 MB) so it was left as PNG.

Live page confirmed still at https://mods.paradoxplaza.com/mods/155583/Windows, now showing Mod Ver. 1.1 with three screenshots.

**Correction to the tenth round**: the `NewVersion` publish that reported success did NOT actually push the new screenshots or the "1.1" version label to the live page - confirmed by loading the page directly and seeing only the original single screenshot and no "MOD VER." row at all. Root cause: `NewVersion` (`ModPublisherCommand=NewVersion`) only uploads new mod file content and a changelog entry; it does not touch page metadata (thumbnail, screenshots, description, tags, mod version display). That's a distinct command, `Update` (`Properties/PublishProfiles/UpdatePublishedConfiguration.pubxml`, `ModPublisherCommand=Update`), which needed to be run *separately* afterward to actually push the metadata changes. Confirmed fixed by re-checking the live page: all 3 screenshots and "MOD VER. 1.1" now show correctly. Lesson: after any publish, verify the actual live page rather than trusting the CLI's "finished successfully" message alone - it only confirms that specific command's own scope succeeded, not that everything the user expects to see actually changed.
