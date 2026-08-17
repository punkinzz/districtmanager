# District Manager

A [Cities: Skylines II](https://mods.paradoxplaza.com/mods/155583/Windows) mod that adds a map-pin button to the toolbar, opening a panel listing every district in your city in one place.

Each district's entry shows:

- Population and average citizen happiness, color-coded
- Its single biggest current complaint
- Services assigned to the district through the game's own "assign district" tool, not buildings that just happen to be nearby
- Parks and any signature or landmark buildings in the district
- Whatever policies are currently active there

Districts sort alphabetically and start collapsed, so expand the ones you care about, or use Expand All / Collapse All. Click a district's pin, or any service or asset chip, and the camera jumps straight to it on the map. There's a manual refresh button if you want to force an update, and you can turn the mod on or off anytime from Options > Mods > District Manager.

It's read-only: it just shows what's already in your city and doesn't change anything.

Get it on [Paradox Mods](https://mods.paradoxplaza.com/mods/155583/Windows).

## Building

Requires the CS2 modding toolchain (Visual Studio 2022 with the .NET desktop workload, plus the `CSII_TOOLPATH` environment variable set by the in-game mod tools installer).

```
dotnet build
```

The UI (`UI/`) is a separate TypeScript/React project built via webpack:

```
cd UI
npm install
npm run build
```

## License

MIT, see [LICENSE](LICENSE).
