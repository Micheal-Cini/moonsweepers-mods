# Builds the Moon Sweepers mods release.
#
#   pwsh build/build.ps1 -Managed "<game>\MoonSweepers_Data\Managed" -Cecil "<folder with Mono.Cecil.dll (0.10.x, net35)>" `
#        -PrivateKey "<path to signing key xml>" -Version 1.2.0
#
# Needs PowerShell 7 (it uses the Roslyn compiler that ships inside PowerShell). The game's own DLLs are
# only used as compile references and are never copied into the release (the installer patches each
# player's own copy).
#
# -Cecil must be a folder holding Mono.Cecil.dll 0.10.4 plus HarmonyX 2.9 (0Harmony.dll,
# MonoMod.RuntimeDetour.dll, MonoMod.Utils.dll), e.g. BepInEx 5.4.23's core folder. Cecil is embedded in
# the installer; Harmony and its dependencies are installed into Mods/lib for mods that patch game code.
#
# Output (dist/): MoonSweepersModsInstaller.exe, MoonSweepers.ModLoader.dll, <Mod>.dll, manifest.txt,
# manifest.txt.sig

param(
    [Parameter(Mandatory = $true)][string]$Managed,
    [Parameter(Mandatory = $true)][string]$Cecil,
    [string]$PrivateKey,
    [string]$PublicKey = "$PSScriptRoot/signing-public-key.xml",
    [string]$Version = "1.2.0",
    [string]$Repo = "Micheal-Cini/moonsweepers-mods"
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root "dist"
if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
New-Item -ItemType Directory -Force -Path $dist | Out-Null

$pwshDir = Split-Path ([System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName) -Parent
Add-Type -Path "$pwshDir/Microsoft.CodeAnalysis.dll"
Add-Type -Path "$pwshDir/Microsoft.CodeAnalysis.CSharp.dll"

function Compile([string]$name, [string[]]$sources, [string[]]$refs, [string]$out, [bool]$exe = $false, [hashtable]$resources = @{}) {
    $popts = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::new([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::CSharp7_3)
    $trees = [System.Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
    foreach ($src in $sources) {
        $code = [IO.File]::ReadAllText($src)
        $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($code, $popts, $src, [Text.Encoding]::UTF8, [Threading.CancellationToken]::None))
    }
    $mrefs = [System.Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
    foreach ($r in $refs) { $mrefs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($r)) }
    $kind = if ($exe) { [Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication } else { [Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary }
    $copts = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new($kind)
    $copts = $copts.WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release).WithPlatform([Microsoft.CodeAnalysis.Platform]::AnyCpu)
    $comp = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create($name, $trees, $mrefs, $copts)
    $res = [System.Collections.Generic.List[Microsoft.CodeAnalysis.ResourceDescription]]::new()
    foreach ($k in $resources.Keys) {
        $path = $resources[$k]
        $bytes = [IO.File]::ReadAllBytes($path)
        $res.Add([Microsoft.CodeAnalysis.ResourceDescription]::new($k, [Func[IO.Stream]]{ [IO.MemoryStream]::new($bytes) }.GetNewClosure(), $true))
    }
    $fs = [IO.File]::Create($out)
    try {
        $emitOpts = [Microsoft.CodeAnalysis.Emit.EmitOptions]::new()
        $result = $comp.Emit($fs, $null, $null, $null, $res, $emitOpts)
    } finally { $fs.Close() }
    $errors = @($result.Diagnostics | Where-Object { $_.Severity -eq [Microsoft.CodeAnalysis.DiagnosticSeverity]::Error })
    if (-not $result.Success) {
        $errors | ForEach-Object { Write-Host $_.ToString() -ForegroundColor Red }
        throw "Build of $name failed"
    }
    Write-Host "built $name -> $out"
}

$sys = @("$Managed/mscorlib.dll", "$Managed/netstandard.dll", "$Managed/System.dll", "$Managed/System.Core.dll")
$unity = @("$Managed/UnityEngine.dll", "$Managed/UnityEngine.CoreModule.dll")

# 1. Mod loader (public key baked in)
$pubXml = ([IO.File]::ReadAllText($PublicKey)).Trim().Replace('"', '\"')
$loaderSrc = Join-Path $dist "ModLoader.generated.cs"
[IO.File]::WriteAllText($loaderSrc, ([IO.File]::ReadAllText("$root/src/ModLoader/ModLoader.cs")).Replace("__PUBLIC_KEY_XML__", $pubXml).Replace('public const string Version = "1.2.0";', "public const string Version = `"$Version`";"))
Compile "MoonSweepers.ModLoader" @($loaderSrc) ($sys + $unity) "$dist/MoonSweepers.ModLoader.dll"
Remove-Item $loaderSrc

# 2. Mods (each folder under src/Mods is one mod DLL)
$libs = @("0Harmony", "MonoMod.RuntimeDetour", "MonoMod.Utils", "Mono.Cecil")
$mods = @()
foreach ($dir in Get-ChildItem "$root/src/Mods" -Directory | Sort-Object Name) {
    $srcs = @(Get-ChildItem $dir.FullName -Filter *.cs -Recurse | ForEach-Object FullName)
    Compile $dir.Name $srcs ($sys + $unity + @("$Managed/Assembly-CSharp.dll", "$Managed/com.rlabrecque.steamworks.net.dll", "$Managed/UnityEngine.IMGUIModule.dll", "$Managed/UnityEngine.TextRenderingModule.dll", "$Managed/UnityEngine.PhysicsModule.dll", "$Managed/UnityEngine.AudioModule.dll", "$Managed/Unity.InputSystem.dll", "$dist/MoonSweepers.ModLoader.dll", "$Cecil/0Harmony.dll")) "$dist/$($dir.Name).dll"
    $mods += $dir.Name
}

# 3. Installer EXE (.NET Framework 4.x, built into Windows), with Cecil + payload embedded
$resources = @{
    "lib/Mono.Cecil.dll" = "$Cecil/Mono.Cecil.dll"
    "payload/MoonSweepers.ModLoader.dll" = "$dist/MoonSweepers.ModLoader.dll"
}
foreach ($m in $mods) { $resources["mods/$m.dll"] = "$dist/$m.dll" }
foreach ($l in $libs) { $resources["modlib/$l.dll"] = "$Cecil/$l.dll" }
Compile "MoonSweepersModsInstaller" @("$root/src/Installer/Installer.cs") ($sys + @("$Cecil/Mono.Cecil.dll")) "$dist/MoonSweepersModsInstaller.exe" $true $resources

# 3b. The installer runs on Windows' built-in .NET Framework 4.x, but is compiled against the game's
#     newer Mono class library. Fail the build if it calls members that .NET Framework 4.8 doesn't have.
$netfxMissing = @(
    "System.String::Trim(System.Char)", "System.String::TrimStart(System.Char)", "System.String::TrimEnd(System.Char)",
    "System.String::Split(System.Char,", "System.String::Split(System.String,", "System.String::Contains(System.Char)",
    "System.String::StartsWith(System.Char)", "System.String::EndsWith(System.Char)", "System.String::Join(System.Char,",
    "System.String::Replace(System.String,System.String,System.StringComparison)", "System.String::Contains(System.String,System.StringComparison)",
    "System.IO.Path::Join(", "System.IO.Path::GetRelativePath(", "KeyValuePair`2::Deconstruct(", "::TryAdd(",
    "System.Linq.Enumerable::ToHashSet(", "System.Math::Clamp(", "System.IO.File::ReadAllTextAsync(", "System.HashCode"
)
Add-Type -Path "$Cecil/Mono.Cecil.dll"
$inst = [Mono.Cecil.AssemblyDefinition]::ReadAssembly("$dist/MoonSweepersModsInstaller.exe")
$bad = @($inst.MainModule.GetMemberReferences() | ForEach-Object { $_.FullName } | Where-Object { $f = $_; $netfxMissing | Where-Object { $f.Contains($_) } } | Sort-Object -Unique)
$inst.Dispose()
if ($bad.Count -gt 0) {
    $bad | ForEach-Object { Write-Host "  not in .NET Framework 4.8: $_" -ForegroundColor Red }
    throw "The installer uses APIs that Windows' built-in .NET Framework doesn't have."
}
Write-Host "installer API check passed"

# 4. Signed manifest
$lines = @("# Moon Sweepers mods release manifest", "release $Version", "loader $Version")
foreach ($m in $mods) {
    $hash = (Get-FileHash "$dist/$m.dll" -Algorithm SHA256).Hash.ToLowerInvariant()
    $lines += "mod $m.dll $hash https://github.com/$Repo/releases/download/v$Version/$m.dll"
}
$manifest = ($lines -join "`n") + "`n"
$manifestBytes = [Text.Encoding]::UTF8.GetBytes($manifest)
[IO.File]::WriteAllBytes("$dist/manifest.txt", $manifestBytes)
if ($PrivateKey) {
    $rsa = [System.Security.Cryptography.RSA]::Create()
    $rsa.FromXmlString([IO.File]::ReadAllText($PrivateKey))
    $sig = $rsa.SignData($manifestBytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    [IO.File]::WriteAllText("$dist/manifest.txt.sig", [Convert]::ToBase64String($sig))
    Write-Host "signed manifest.txt"
} else {
    Write-Warning "No -PrivateKey given: manifest is NOT signed and loaders will ignore it."
}
Write-Host "release $Version ready in $dist"
