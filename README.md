# Modern Weapon Mods (MWM) — Source Code

This repo contains the C# source for the two compiled components shipped by the
[Modern Weapon Mods](https://hub.sp-tarkov.com/) SPT mod (`org.kilodyne.modernweaponmods`).
It does **not** contain the mod's item data, 3D models, or textures — just the code that
compiles into the two DLLs the mod ships.

## Layout

- **`ModernWeaponMods/`** — the SPT server mod (`ModernWeaponMods.dll`). Registers item
  metadata with the server and patches attachment-slot compatibility at boot via WTT-ServerCommonLib.
- **`ModernWeaponModsClientBootstrap/`** — a BepInEx client plugin
  (`ModernWeaponModsClientBootstrap.dll`) with a handful of Harmony patches fixing
  client-side rendering/build-screen bugs that the server-side item data can't express alone.

## Building

Each folder has its own `build.ps1` and `.csproj`. Both require:
- .NET 10 SDK
- A local SPT 4.1.x server install (4.1.6 tested) and WTT-ServerCommonLib 3.x
- A local SPT/BepInEx client install (`Assembly-CSharp.dll` etc. referenced by
  `ModernWeaponModsClientBootstrap.csproj`)

To build the server mod against custom install paths, pass `SptDir` and `WttLibDir`:

```powershell
dotnet build .\ModernWeaponMods\ModernWeaponMods.csproj -c Release `
  -p:SptDir="D:\path\to\SPT_Runtime" `
  -p:WttLibDir="D:\path\to\SPT_Runtime\user\mods\WTT-ServerCommonLib"
```

`build.ps1` in each folder builds and deploys to a local SPT install — adjust the paths at
the top of each script to match your own setup.

## License

See [LICENSE](LICENSE).
