// Moon Sweepers Mods Installer
// Built by VettaTech (https://vettatech.ca). Source: https://github.com/Micheal-Cini/moonsweepers-mods
//
// Install / repair:
//   - finds the game through Steam,
//   - cleans up the old BepInEx-based version (v1.0) and the single-mod version (v1.1),
//   - backs up the game's Assembly-CSharp.dll and adds one call that starts the mod loader,
//   - installs the mod loader and the bundled mods.
// Run it again after every game update (Steam restores the original game file when it updates).
// Uninstall removes the call and the loader; Steam's "Verify integrity of game files" also undoes it.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace MoonSweepersModsInstaller
{
    internal static class Program
    {
        private const string Version = "1.2.0";
        private const string AppId = "5335510";
        private const string GameFolderName = "Moon Sweepers";
        private const string GameExe = "MoonSweepers.exe";
        private const string LoaderAssembly = "MoonSweepers.ModLoader";
        private const string LoaderType = "MoonModLoader.Loader, MoonSweepers.ModLoader";
        private const string Website = "https://vettatech.ca";
        private const string RepoUrl = "https://github.com/Micheal-Cini/moonsweepers-mods";
        private static bool backupWritten;

        private static int Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbedded;
            try
            {
                Console.Title = "Moon Sweepers Mods Installer";
            }
            catch
            {
            }
            int code = 0;
            try
            {
                code = Run(args);
            }
            catch (UnauthorizedAccessException e)
            {
                Error("Windows blocked access to the game folder: " + e.Message);
                Error("Close the game, then right-click the installer and choose \"Run as administrator\".");
                code = 5;
            }
            catch (Exception e)
            {
                Error("Something went wrong: " + e.Message);
                Console.WriteLine();
                Console.WriteLine(e);
                code = 1;
            }
            bool quiet = Array.Exists(args, a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase));
            if (!quiet)
            {
                Console.WriteLine();
                Console.Write("Press Enter to close.");
                Console.ReadLine();
            }
            return code;
        }

        // Mono.Cecil and the payload DLLs are embedded so this is a single EXE.
        private static Assembly ResolveEmbedded(object sender, ResolveEventArgs e)
        {
            string name = new AssemblyName(e.Name).Name;
            byte[] data = Resource("lib/" + name + ".dll");
            return data != null ? Assembly.Load(data) : null;
        }

        private static byte[] Resource(string name)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (s == null)
                {
                    return null;
                }
                MemoryStream ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        private static IEnumerable<string> Payload(string prefix)
        {
            foreach (string n in Assembly.GetExecutingAssembly().GetManifestResourceNames())
            {
                if (n.StartsWith(prefix, StringComparison.Ordinal))
                {
                    yield return n.Substring(prefix.Length);
                }
            }
        }

        // ------------------------------------------------------------------ UI helpers

        private static void Color(ConsoleColor c, string s)
        {
            ConsoleColor old = Console.ForegroundColor;
            Console.ForegroundColor = c;
            Console.WriteLine(s);
            Console.ForegroundColor = old;
        }

        private static void Step(string s) { Color(ConsoleColor.Cyan, "  > " + s); }
        private static void Ok(string s) { Color(ConsoleColor.Green, "  OK " + s); }
        private static void Warn(string s) { Color(ConsoleColor.Yellow, "  ! " + s); }
        private static void Error(string s) { Color(ConsoleColor.Red, "  X " + s); }

        private static void Banner()
        {
            Console.WriteLine();
            Color(ConsoleColor.White, "  =====================================================");
            Color(ConsoleColor.White, "   MOON SWEEPERS MODS  -  Installer v" + Version);
            Color(ConsoleColor.Gray, "   Built by VettaTech  |  " + Website);
            Color(ConsoleColor.White, "  =====================================================");
            Console.WriteLine();
        }

        // ------------------------------------------------------------------ main flow

        private static int Run(string[] args)
        {
            Banner();
            bool uninstall = Array.Exists(args, a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));
            string gameDir = null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].Equals("--game", StringComparison.OrdinalIgnoreCase))
                {
                    gameDir = args[i + 1];
                }
            }
            bool quiet = Array.Exists(args, a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase));

            if (!uninstall && !quiet)
            {
                Console.WriteLine("  What would you like to do?");
                Console.WriteLine("    [1] Install or repair mods  (default, also use this after a game update)");
                Console.WriteLine("    [2] Uninstall mods");
                Console.Write("  Choice (press Enter for 1): ");
                string choice = (Console.ReadLine() ?? "").Trim();
                uninstall = choice == "2";
                Console.WriteLine();
            }

            if (gameDir == null)
            {
                Step("Looking for Moon Sweepers...");
                gameDir = FindGame();
            }
            while (gameDir == null || !File.Exists(Path.Combine(gameDir, GameExe)))
            {
                if (quiet)
                {
                    Error("Could not find Moon Sweepers. Use --game \"<folder>\".");
                    return 2;
                }
                Warn("Could not find the game automatically.");
                Console.WriteLine("  In Steam: right-click Moon Sweepers > Manage > Browse local files, copy the");
                Console.WriteLine("  folder address from the top of the window and paste it here.");
                Console.Write("  Game folder: ");
                gameDir = (Console.ReadLine() ?? "").Trim().Trim(new[] { '"' });
                if (gameDir.Length == 0)
                {
                    return 2;
                }
            }
            Ok("Found the game: " + gameDir);

            while (Process.GetProcessesByName("MoonSweepers").Length > 0)
            {
                if (quiet)
                {
                    Error("Moon Sweepers is running. Close it first.");
                    return 3;
                }
                Warn("Moon Sweepers is running. Please close the game, then press Enter.");
                Console.ReadLine();
            }

            string managed = Path.Combine(Path.Combine(gameDir, "MoonSweepers_Data"), "Managed");
            string asmPath = Path.Combine(managed, "Assembly-CSharp.dll");
            if (!File.Exists(asmPath))
            {
                Error("The game files look incomplete (no Assembly-CSharp.dll). Verify the game in Steam and try again.");
                return 4;
            }

            CleanOldVersions(gameDir, managed);

            if (uninstall)
            {
                Step("Removing the mod loader from the game...");
                int removed = PatchGame(asmPath, managed, false);
                DeleteIfExists(Path.Combine(managed, LoaderAssembly + ".dll"));
                Ok(removed > 0 ? "Mod loader removed." : "The game was not patched.");
                string mods = Path.Combine(gameDir, "Mods");
                if (Directory.Exists(mods) && !quiet)
                {
                    Console.Write("  Also delete the Mods folder? (y/N): ");
                    if ((Console.ReadLine() ?? "").Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase))
                    {
                        Directory.Delete(mods, true);
                        Ok("Mods folder deleted.");
                    }
                }
                Console.WriteLine();
                Color(ConsoleColor.Green, "  Done. The game is back to normal. Upgrades already shared in a save stay shared.");
                return 0;
            }

            Step("Installing the mod loader...");
            File.WriteAllBytes(Path.Combine(managed, LoaderAssembly + ".dll"), Resource("payload/" + LoaderAssembly + ".dll"));
            Ok("Mod loader v" + Version + " installed.");

            Step("Connecting the loader to the game...");
            PatchGame(asmPath, managed, true);
            Ok(backupWritten ? "Game connected (clean copy of the game file saved as Assembly-CSharp.dll.original)." : "Game connected.");

            Step("Installing mods...");
            string modsDir = Path.Combine(gameDir, "Mods");
            Directory.CreateDirectory(modsDir);
            string libDir = Path.Combine(modsDir, "lib");
            Directory.CreateDirectory(libDir);
            foreach (string lib in Payload("modlib/"))
            {
                File.WriteAllBytes(Path.Combine(libDir, lib), Resource("modlib/" + lib));
            }
            Ok("Shared libraries installed (Harmony).");
            foreach (string mod in Payload("mods/"))
            {
                string target = Path.Combine(modsDir, mod);
                byte[] data = Resource("mods/" + mod);
                if (!File.Exists(target))
                {
                    File.WriteAllBytes(target, data);
                    Ok(mod + " installed.");
                }
                else
                {
                    Ok(mod + " already installed (it keeps itself up to date).");
                }
            }

            Console.WriteLine();
            Color(ConsoleColor.Green, "  All done! Start Moon Sweepers from Steam as usual.");
            Console.WriteLine();
            Console.WriteLine("  - Mods update themselves from GitHub when the game starts (the update applies");
            Console.WriteLine("    the launch after it downloads).");
            Console.WriteLine("  - After a game update, just run this installer again.");
            Console.WriteLine("  - Mods and updates: " + RepoUrl);
            Console.WriteLine();
            Color(ConsoleColor.Gray, "  Need a website or a custom app? VettaTech builds them: " + Website);
            return 0;
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        // ------------------------------------------------------------------ cleanup of older versions

        private static void CleanOldVersions(string gameDir, string managed)
        {
            // v1.0 used BepInEx, which crashes this Unity version. Only remove it when it is clearly our
            // old install (it contains our TeamUpgrades plugin); otherwise just switch the loader off.
            string winhttp = Path.Combine(gameDir, "winhttp.dll");
            string doorstop = Path.Combine(gameDir, "doorstop_config.ini");
            string bep = Path.Combine(gameDir, "BepInEx");
            bool ours = Directory.Exists(Path.Combine(Path.Combine(bep, "plugins"), "TeamUpgrades"));
            if (ours)
            {
                Step("Removing the old BepInEx version of Team Upgrades (BepInEx crashes this game)...");
                DeleteIfExists(winhttp);
                DeleteIfExists(doorstop);
                DeleteIfExists(Path.Combine(gameDir, "changelog.txt"));
                try
                {
                    Directory.Delete(bep, true);
                }
                catch (Exception e)
                {
                    Warn("Could not delete the BepInEx folder (" + e.Message + "). It is harmless; delete it later if you like.");
                }
                Ok("Old BepInEx files removed.");
            }
            else if (File.Exists(doorstop) && File.Exists(winhttp))
            {
                string text = File.ReadAllText(doorstop);
                string off = Regex.Replace(text, @"(?m)^(\s*enabled\s*=\s*)true", "${1}false");
                if (off != text)
                {
                    File.WriteAllText(doorstop, off);
                    Warn("Found BepInEx in the game folder. It crashes this version of the game, so it was switched");
                    Warn("off (doorstop_config.ini: enabled = false). Your BepInEx files were not deleted.");
                }
            }
            // v1.1 was a single mod dropped into Managed.
            string v11 = Path.Combine(managed, "MoonSweepers.TeamUpgrades.dll");
            if (File.Exists(v11))
            {
                File.Delete(v11);
                Ok("Old Team Upgrades v1.1 file removed (now runs from the Mods folder).");
            }
            DeleteIfExists(Path.Combine(gameDir, "TeamUpgrades-README.txt"));
        }

        // ------------------------------------------------------------------ finding the game

        private static string FindGame()
        {
            // Run from inside the game folder?
            string here = AppDomain.CurrentDomain.BaseDirectory;
            if (File.Exists(Path.Combine(here, GameExe)))
            {
                return here;
            }
            // Steam writes an uninstall entry with the install folder.
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (RegistryKey k = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App " + AppId))
                    {
                        string loc = k != null ? k.GetValue("InstallLocation") as string : null;
                        if (!string.IsNullOrEmpty(loc) && File.Exists(Path.Combine(loc, GameExe)))
                        {
                            return loc;
                        }
                    }
                }
                catch
                {
                }
            }
            // Steam libraries.
            List<string> steamRoots = new List<string>();
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    string p = k != null ? k.GetValue("SteamPath") as string : null;
                    if (!string.IsNullOrEmpty(p))
                    {
                        steamRoots.Add(p.Replace('/', '\\'));
                    }
                }
            }
            catch
            {
            }
            steamRoots.Add(@"C:\Program Files (x86)\Steam");
            List<string> libraries = new List<string>();
            foreach (string root in steamRoots)
            {
                libraries.Add(root);
                string vdf = Path.Combine(Path.Combine(root, "steamapps"), "libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    {
                        libraries.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
                    }
                }
            }
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType == DriveType.Fixed)
                    {
                        libraries.Add(Path.Combine(d.RootDirectory.FullName, "SteamLibrary"));
                    }
                }
                catch
                {
                }
            }
            foreach (string lib in libraries)
            {
                string apps = Path.Combine(lib, "steamapps");
                string folder = GameFolderName;
                string acf = Path.Combine(apps, "appmanifest_" + AppId + ".acf");
                try
                {
                    if (File.Exists(acf))
                    {
                        Match m = Regex.Match(File.ReadAllText(acf), "\"installdir\"\\s+\"([^\"]+)\"");
                        if (m.Success)
                        {
                            folder = m.Groups[1].Value;
                        }
                    }
                }
                catch
                {
                }
                string dir = Path.Combine(Path.Combine(apps, "common"), folder);
                if (File.Exists(Path.Combine(dir, GameExe)))
                {
                    return dir;
                }
            }
            return null;
        }

        // ------------------------------------------------------------------ patching

        // Our hook is 13 IL instructions starting with ldstr "<Type>.Loader, MoonSweepers.<Assembly>".
        private static bool IsOurHook(Instruction ins)
        {
            string s = ins.OpCode == OpCodes.Ldstr ? ins.Operand as string : null;
            return s != null && s.Contains(".Loader, MoonSweepers.");
        }

        private static int RemoveHooks(ModuleDefinition module)
        {
            int removed = 0;
            foreach (TypeDefinition t in module.GetTypes())
            {
                foreach (MethodDefinition m in t.Methods)
                {
                    if (!m.HasBody)
                    {
                        continue;
                    }
                    IList<Instruction> body = m.Body.Instructions;
                    for (int i = 0; i < body.Count; i++)
                    {
                        if (!IsOurHook(body[i]) || i + 13 > body.Count)
                        {
                            continue;
                        }
                        if (body[i + 4].OpCode.FlowControl != FlowControl.Cond_Branch || body[i + 11].OpCode.FlowControl != FlowControl.Branch || body[i + 12].OpCode != OpCodes.Pop)
                        {
                            throw new InvalidOperationException("Found an unexpected mod hook in " + t.Name + "." + m.Name + ". Verify the game files in Steam, then run this again.");
                        }
                        ILProcessor il = m.Body.GetILProcessor();
                        for (int k = 0; k < 13; k++)
                        {
                            il.Remove(body[i]);
                        }
                        removed++;
                        i--;
                    }
                }
            }
            return removed;
        }

        private static MethodDefinition FindHookTarget(ModuleDefinition module, string managed)
        {
            TypeDefinition bench = module.GetType("MoonCleaners.MoonBenchmarkRun");
            if (bench != null)
            {
                foreach (MethodDefinition m in bench.Methods)
                {
                    if (m.Name == "FromCommandLine" && m.IsStatic && m.HasBody && m.Parameters.Count == 0)
                    {
                        return m;
                    }
                }
            }
            // Fallback: any AfterSceneLoad / BeforeSceneLoad startup method listed by Unity.
            string json = Path.Combine(Path.GetDirectoryName(managed), "RuntimeInitializeOnLoads.json");
            if (File.Exists(json))
            {
                string text = File.ReadAllText(json);
                foreach (int wanted in new[] { 0, 1 })
                {
                    foreach (Match mm in Regex.Matches(text, "\\{[^{}]*\\}"))
                    {
                        string o = mm.Value;
                        if (!o.Contains("\"assemblyName\":\"Assembly-CSharp\"") && !o.Contains("\"assemblyName\": \"Assembly-CSharp\""))
                        {
                            continue;
                        }
                        Match lt = Regex.Match(o, "\"loadTypes\"\\s*:\\s*(\\d+)");
                        if (!lt.Success || int.Parse(lt.Groups[1].Value) != wanted)
                        {
                            continue;
                        }
                        string ns = Regex.Match(o, "\"nameSpace\"\\s*:\\s*\"([^\"]*)\"").Groups[1].Value;
                        string cls = Regex.Match(o, "\"className\"\\s*:\\s*\"([^\"]*)\"").Groups[1].Value;
                        string meth = Regex.Match(o, "\"methodName\"\\s*:\\s*\"([^\"]*)\"").Groups[1].Value;
                        TypeDefinition td = module.GetType(ns.Length > 0 ? ns + "." + cls : cls);
                        if (td == null)
                        {
                            continue;
                        }
                        foreach (MethodDefinition m in td.Methods)
                        {
                            if (m.Name == meth && m.IsStatic && m.HasBody && m.Parameters.Count == 0)
                            {
                                return m;
                            }
                        }
                    }
                }
            }
            return null;
        }

        private static int PatchGame(string asmPath, string managed, bool install)
        {
            byte[] original = File.ReadAllBytes(asmPath);
            DefaultAssemblyResolver resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(managed);
            ReaderParameters rp = new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate };
            int removed;
            using (AssemblyDefinition asm = AssemblyDefinition.ReadAssembly(new MemoryStream(original), rp))
            {
                ModuleDefinition module = asm.MainModule;
                removed = RemoveHooks(module);
                if (removed == 0)
                {
                    // A clean game file (fresh from Steam): keep it as the backup.
                    File.WriteAllBytes(asmPath + ".original", original);
                    backupWritten = true;
                }
                if (install)
                {
                    MethodDefinition target = FindHookTarget(module, managed);
                    if (target == null)
                    {
                        throw new InvalidOperationException("This version of the game changed too much for the installer. Check " + RepoUrl + " for an update.");
                    }
                    InjectHook(module, target);
                }
                else if (removed == 0)
                {
                    return 0;
                }
                MemoryStream outStream = new MemoryStream();
                asm.Write(outStream);
                // Write next to it, then swap, so an interruption can't leave a half-written game file.
                string tmp = asmPath + ".new";
                File.WriteAllBytes(tmp, outStream.ToArray());
                File.Copy(tmp, asmPath, true);
                File.Delete(tmp);
            }
            return removed;
        }

        private static void InjectHook(ModuleDefinition module, MethodDefinition method)
        {
            AssemblyNameReference corlib = null;
            foreach (AssemblyNameReference r in module.AssemblyReferences)
            {
                if (r.Name == "netstandard")
                {
                    corlib = r;
                }
            }
            if (corlib == null)
            {
                foreach (AssemblyNameReference r in module.AssemblyReferences)
                {
                    if (r.Name == "mscorlib")
                    {
                        corlib = r;
                    }
                }
            }
            if (corlib == null)
            {
                throw new InvalidOperationException("Unexpected game file layout (no core library reference).");
            }
            TypeSystem ts = module.TypeSystem;
            TypeReference tType = new TypeReference("System", "Type", module, corlib);
            TypeReference tMethodInfo = new TypeReference("System.Reflection", "MethodInfo", module, corlib);
            TypeReference tMethodBase = new TypeReference("System.Reflection", "MethodBase", module, corlib);

            MethodReference getType = new MethodReference("GetType", tType, tType);
            getType.Parameters.Add(new ParameterDefinition(ts.String));
            getType.Parameters.Add(new ParameterDefinition(ts.Boolean));

            MethodReference getMethod = new MethodReference("GetMethod", tMethodInfo, tType) { HasThis = true };
            getMethod.Parameters.Add(new ParameterDefinition(ts.String));

            MethodReference invoke = new MethodReference("Invoke", ts.Object, tMethodBase) { HasThis = true };
            invoke.Parameters.Add(new ParameterDefinition(ts.Object));
            invoke.Parameters.Add(new ParameterDefinition(new ArrayType(ts.Object)));

            ILProcessor il = method.Body.GetILProcessor();
            Instruction first = method.Body.Instructions[0];
            Instruction popSkip = il.Create(OpCodes.Pop);
            Instruction[] seq =
            {
                il.Create(OpCodes.Ldstr, LoaderType),
                il.Create(OpCodes.Ldc_I4_0),
                il.Create(OpCodes.Call, getType),
                il.Create(OpCodes.Dup),
                il.Create(OpCodes.Brfalse, popSkip),
                il.Create(OpCodes.Ldstr, "Boot"),
                il.Create(OpCodes.Callvirt, getMethod),
                il.Create(OpCodes.Ldnull),
                il.Create(OpCodes.Ldnull),
                il.Create(OpCodes.Callvirt, invoke),
                il.Create(OpCodes.Pop),
                il.Create(OpCodes.Br, first),
                popSkip,
            };
            foreach (Instruction i in seq)
            {
                il.InsertBefore(first, i);
            }
            if (method.Body.MaxStackSize < 4)
            {
                method.Body.MaxStackSize = 4;
            }
        }
    }
}
