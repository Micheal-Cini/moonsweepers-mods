# Building a release

Everything builds with **PowerShell 7** (it contains the C# compiler). No Visual Studio needed.

## You need

- PowerShell 7 (`pwsh`)
- The game's `MoonSweepers_Data\Managed` folder (used only as compile references; never committed or shipped)
- `Mono.Cecil.dll` 0.10.x (the net35 build, e.g. from BepInEx 5's `core` folder). It is embedded in the installer.
- The **private signing key** (`moonsweepers-mods-signing-PRIVATE.xml`). Keep it secret and backed up; never commit it. Without it, loaders will reject a release. The public half is `build/signing-public-key.xml`.

## Build

```powershell
pwsh build/build.ps1 `
  -Managed "D:\SteamLibrary\steamapps\common\Moon Sweepers\MoonSweepers_Data\Managed" `
  -Cecil "C:\path\to\folder-with-Mono.Cecil" `
  -PrivateKey "C:\path\to\moonsweepers-mods-signing-PRIVATE.xml" `
  -Version 1.2.1
```

Output goes to `dist/`.

## Release

1. Copy everything in `dist/` (installer, loader, mod DLLs, `manifest.txt`, `manifest.txt.sig`) into `releases/v<Version>/` and add a `NOTES.md` there (first line `# Title`, the rest is the release description).
2. Commit, then push a tag: `git tag v<Version> && git push origin v<Version>`.
3. The **Publish release** GitHub Action creates the release and attaches the files.

Loaders read `releases/latest/download/manifest.txt`, so the newest release is what players get.

## Adding a mod

Add a folder under `src/Mods/<ModName>/` with C# files. Provide:

```csharp
public static class ModEntry
{
    public static void Init() { /* called once after the game's first scene loads */ }
}
```

The build compiles each folder into `<ModName>.dll`, adds it to the manifest and bundles it into the installer.

## After a game update

Usually nothing changes: players just re-run the installer. If the game's code changed in a way that breaks a mod, rebuild against the new `Managed` folder and publish a new release.
