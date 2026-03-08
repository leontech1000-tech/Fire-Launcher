# Fire Launcher

Open-source Windows launcher shell for Minecraft Legacy Console Edition forks.

## Project layout

- `assets/backgrounds/` stores scenic background art.
- `assets/logos/` stores the launcher logo assets.
- `src/FireLauncher.App/` contains the WPF desktop launcher.

## Runtime data

The app now writes profile data outside the repo:

- `%LOCALAPPDATA%\FireLauncher\launcher-settings.json`
- `%LOCALAPPDATA%\FireLauncher\Profiles\<ProfileName>\forkdb.json`
- `%LOCALAPPDATA%\FireLauncher\TestForks\`

Each profile folder gets its own `forkdb.json` file that stores fork metadata such as:

- install folder
- executable path
- whether the fork has multiplayer
- whether the fork supports `-ip`, `-name`, or a port argument

The launcher currently seeds known local installs for:

- `LCEMP v1.0.3`
- `MinecraftConsoles` from `LCEWindows64`

## Build

This repo is set up for Visual Studio 2022 and .NET Framework 4.7.2.

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" FireLauncher.sln /t:Build /p:Configuration=Debug
```

## Run

```powershell
.\src\FireLauncher.App\bin\Debug\FireLauncher.App.exe
```
