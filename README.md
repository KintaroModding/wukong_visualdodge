# Visual Dodge

Makes Wukong's staff glow shortly before an enemy attack opens the perfect dodge window of Black Myth: Wukong.

A C# mod for Black Myth: Wukong, loaded by [C# Loader](https://github.com/KintaroModding/b1csharploader), made with game
version `1.0.21.23831` and C# Loader v1.0.1. This README is for developers; what the mod does and how
to install it are in [openmods.md](openmods.md), the mod page on [openmods](https://openmods.nexus).

## How it works

The game opens the player's perfect-dodge window when an enemy's attack warning reaches the player
(`BUS_PreciseDodgeComp.SweepCheckInPreciseDodgeRange`): it sets `BUC_PreciseDodgeData.CurrentPreciseDodgeTimeSeconds`
to `PreciseDodgeTolerantTimeSeconds` (0.03 s in game version 1.0.21) on every frame of that contact and counts it down.
So the window lasts as long as the warning touches the player, plus 0.03 s. A dodge press while it is above 0 is a
perfect dodge, unless `CanPreciseDodge` is false or the player has the simple state `CantTriggerPreciseDodge` or
`InAnimationSyncing` (or `Beatback`, when the cvar `b.PreciseDodge.BeatbackCanTrigger` is 0).

A melee attack opens the window through the notify state `BANS_GSAttackWarnning` in the enemy's attack montage: while
it runs, its warning shape is swept, and touching the player opens the window.

- `Ticker.cs`: runs the mod once per frame on the game thread, without hooks. The game instance
  (`BGWGameInstanceCS`) calls `OnTickWithGroup` on every entry of its private list `TickList`; a background thread
  adds the ticker once the game instance is ready (`InitGameInstObjDone`) by setting a copy of the list that holds it,
  so the game thread never walks a list that changes. Tick group 16384: after the frame's game logic.
- `AttackPredictor.cs`: every 0.5 s the enemies (`BGU_CharacterAI`, `BGUFunctionLibraryCS.BGUIsEnemyTeam`) within
  15 m; every frame, for those whose lock-on target (`BUC_TargetInfoData`) is the player: the playing montage, its
  position and play rate (`UAnimInstance.Montage_GetPosition`, `Montage_GetPlayRate`), its warning notifies and speed
  changes (`UBGUFuncLibSkill.BGUGetMontageNotifies`, `UGSE_AnimFuncLib.GetNotifyTriggerTime`; read once per montage;
  `BANS_GSSetAMSpeedRate.SectionSpeedRate`), the current and the next section (`Montage_GetCurrentSection`,
  `UGSE_AnimFuncLib.GetMontageNextSection`, `UBGUFunctionLibrary.BGUGetMontageSectionStartAndEndTime`) and the time
  dilation of the world and of the enemy. From these: the real seconds until each coming warning notify starts.
- `DodgeFlash.cs`: when a coming warning is `ReactionSeconds` or less away and none of the conditions above fails,
  the character glows (once per warning). A window that opens without a glow shortly before it (projectiles, ground
  effects, attacks that were not predicted) gets the glow at once. The glow is one of the game's own display clips
  for Wukong (a DBC data asset in `/Game/00Main/VFX/Characters/sunwukong/DBC/`, `Clip` in `config.json`; default
  `Talent/DBC_wukong_buff_precisedodge`), played on the character through his clip manager
  (`BUS_DispLibDBCManageComp`, reached through `BUS_DispLibEventCollection.Evt_RequestApplyOneDBCDataAsset`) and ended
  after `FlashSeconds` (`Evt_RequestEndDBCEffects`). The clip asset comes from the game's asset cache
  (`BGW_PreloadAssetMgr.TryGetCachedResourceObj`). The log has the first predicted attacks, how long after each glow
  the window really opened, a summary every 20 windows, and once per clip the clip's parts.
- The default clip turns up the emissive material layer `ML_Emissive_JGB` (parameters `B_Power_intensity` and
  `FX_GunAlpha`) of the meshes tagged `Weapon`, so the staff glows, and plays a particle effect. Other clips of the
  folder work too, for example `XuliBaofa/DBC_XuLi_Baofa_1` (the flash of a reached charge level, with its sound).
- Not used: the game's rim light "ArtFresnel". The game drives it with `BUS_DispLibUnitArtFresnelComp`, which it
  gives to no character, and setting its parameters in Wukong's materials directly showed nothing (1.1 to 1.3).
- `Config.cs`: reads `config.json` (the game's LitJSON).

The mod changes no game data.

## Build and deploy

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1 -GameDll <folder> -ModBase <file>   # build into dist\mod\wukong_visualdodge
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Deploy                             # build, then copy into the game
```

The build needs:

- the .NET SDK 8 or newer (https://dotnet.microsoft.com);
- the game's C# DLLs: the folder `GameDll` of the C# Loader repository (`-GameDll`), made for game version
  `1.0.21.23831`;
- `CSharpModBase.dll`, the mod API of C# Loader: `b1\Binaries\Win64\CSharpLoader\CSharpModBase.dll` in its release zip
  (`-ModBase`). Without `-ModBase` the build takes it from the loader installed in the game.

`build.ps1` builds `src\wukong_visualdodge.csproj` with the version of `mod.json`, puts the DLL and the files of
`src\files\` (when there) into `dist\mod\wukong_visualdodge\`, and checks that the DLL has that version and needs no
assembly that the game or C# Loader lacks. `deploy.ps1` copies that folder to
`<game folder>\b1\Binaries\Win64\CSharpLoader\Mods\wukong_visualdodge\`. The game folder is
`$env:UE5_GAMES_DIR\Black Myth Wukong`; `-GameDir` or `-ModsDir` override it. The running game locks the DLL: close the
game first, or use `deploy.ps1 -Wait`.

The mod writes its lines into `wukong_visualdodge.log` in its folder and into the loader's console (`Console=1` in
`CSharpLoader\b1cs.ini`). With `Develop=1`, Ctrl+F5 in the game reloads all C# mods.

Versioning: every change raises the second number (1.0 -> 1.1) in `mod.json`: `python scripts\bump_version.py`. The
build writes it into the DLL.

## Project layout

```
src/wukong_visualdodge.csproj   The C# project (.NET Framework 4.7.2)
src/*.cs                    The mod: ModMain.cs (entry point), Ticker.cs, AttackPredictor.cs, DodgeFlash.cs, Config.cs, ModLog.cs (log)
src/files/config.json       The default settings, next to the DLL in the mod folder
src/Directory.Build.props   Sends the build output to dist\
docs/                       Cover and screenshots
scripts/                    bump_version.py
build.ps1                   Builds and checks the mod folder
deploy.ps1                  Copies the mod folder into the game
mod.json                    Name, version, game and C# Loader version
```

## License

MIT, see [LICENSE](LICENSE).
