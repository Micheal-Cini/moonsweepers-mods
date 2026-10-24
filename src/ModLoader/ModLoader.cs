// Moon Sweepers Mod Loader
// Built by VettaTech (https://vettatech.ca). Source: https://github.com/Micheal-Cini/moonsweepers-mods
//
// The installer adds one call to the game's code that runs MoonModLoader.Loader.Boot() once the first
// scene has loaded. Boot():
//   1. applies any updates downloaded last session (Mods/.pending),
//   2. loads every Mods/*.dll and calls its public static ModEntry.Init(),
//   3. checks GitHub in the background for a newer, signed set of mods and downloads them for next launch.
//
// Updates are only accepted if manifest.txt is signed with the VettaTech mods key (RSA-SHA256) and each
// file matches the SHA-256 listed in it. Put an empty file named "no-update.txt" in the Mods folder to
// turn auto-update off.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;

namespace MoonModLoader
{
    public static class Loader
    {
        public const string Version = "1.2.0";
        public const string Repo = "Micheal-Cini/moonsweepers-mods";
        public const string ManifestUrl = "https://github.com/" + Repo + "/releases/latest/download/manifest.txt";
        public const string SignatureUrl = "https://github.com/" + Repo + "/releases/latest/download/manifest.txt.sig";

        // Public half of the VettaTech mods signing key. Replaced at build time.
        private const string PublicKeyXml = "__PUBLIC_KEY_XML__";

        private static bool booted;
        private static string modsDir;
        private static readonly List<string> loaded = new List<string>();

        public static string ModsDirectory { get { return modsDir; } }
        public static IList<string> LoadedMods { get { return loaded.AsReadOnly(); } }

        public static void Boot()
        {
            if (booted)
            {
                return;
            }
            booted = true;
            try
            {
                modsDir = Path.GetFullPath(Path.Combine(Path.Combine(Application.dataPath, ".."), "Mods"));
                Directory.CreateDirectory(modsDir);
                Log("Moon Sweepers Mod Loader v" + Version + " (by VettaTech)");
                AppDomain.CurrentDomain.AssemblyResolve += ResolveLib;
                ApplyPending();
                LoadMods();
                if (!File.Exists(Path.Combine(modsDir, "no-update.txt")))
                {
                    Thread t = new Thread(UpdateCheck);
                    t.IsBackground = true;
                    t.Name = "MoonModLoader update";
                    t.Start();
                }
            }
            catch (Exception e)
            {
                Log("startup failed: " + e);
            }
        }

        public static void Log(string msg)
        {
            Debug.Log("[ModLoader] " + msg);
        }

        // Shared libraries for mods (for example Harmony) live in Mods/lib.
        private static Assembly ResolveLib(object sender, ResolveEventArgs e)
        {
            try
            {
                string name = new AssemblyName(e.Name).Name;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.GetName().Name == name)
                    {
                        return a;
                    }
                }
                string path = Path.Combine(Path.Combine(modsDir, "lib"), name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            }
            catch
            {
                return null;
            }
        }

        private static string PendingDir
        {
            get { return Path.Combine(modsDir, ".pending"); }
        }

        private static void ApplyPending()
        {
            string pending = PendingDir;
            if (!Directory.Exists(pending))
            {
                return;
            }
            foreach (string file in Directory.GetFiles(pending))
            {
                string name = Path.GetFileName(file);
                try
                {
                    if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(file); // interrupted download
                    }
                    else if (name.EndsWith(".delete", StringComparison.OrdinalIgnoreCase))
                    {
                        string mod = name.Substring(0, name.Length - ".delete".Length);
                        string target = Path.Combine(modsDir, mod);
                        if (SafeName(mod) && File.Exists(target))
                        {
                            File.Delete(target);
                            Log("removed " + mod);
                        }
                        File.Delete(file);
                    }
                    else if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        string hashFile = file + ".sha256";
                        string expected = File.Exists(hashFile) ? File.ReadAllText(hashFile).Trim() : null;
                        if (!SafeName(name) || expected == null || Sha256(File.ReadAllBytes(file)) != expected)
                        {
                            File.Delete(file);
                            if (File.Exists(hashFile))
                            {
                                File.Delete(hashFile);
                            }
                            Log("discarded a damaged update for " + name + "; it will download again.");
                            continue;
                        }
                        string target = Path.Combine(modsDir, name);
                        if (File.Exists(target))
                        {
                            File.Delete(target);
                        }
                        File.Move(file, target);
                        File.Delete(hashFile);
                        Log("updated " + name);
                    }
                }
                catch (Exception e)
                {
                    Log("could not apply update " + name + ": " + e.Message);
                }
            }
        }

        private static void LoadMods()
        {
            string[] files = Directory.GetFiles(modsDir, "*.dll");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                string name = Path.GetFileName(file);
                try
                {
                    Assembly asm = Assembly.LoadFrom(file);
                    bool started = false;
                    foreach (Type t in asm.GetTypes())
                    {
                        if (t.Name != "ModEntry")
                        {
                            continue;
                        }
                        MethodInfo init = t.GetMethod("Init", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                        if (init != null)
                        {
                            init.Invoke(null, null);
                            started = true;
                        }
                    }
                    if (started)
                    {
                        loaded.Add(name);
                        Log("loaded " + name);
                    }
                    else
                    {
                        Log(name + " has no ModEntry.Init(); skipped.");
                    }
                }
                catch (Exception e)
                {
                    Exception inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                    Log("failed to load " + name + ": " + inner);
                }
            }
        }

        // ---------------------------------------------------------------- updates

        private static byte[] Download(string url)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = 15000;
            req.ReadWriteTimeout = 30000;
            req.AllowAutoRedirect = true;
            req.UserAgent = "MoonSweepersModLoader/" + Version;
            using (WebResponse resp = req.GetResponse())
            using (Stream s = resp.GetResponseStream())
            using (MemoryStream ms = new MemoryStream())
            {
                byte[] buf = new byte[65536];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    ms.Write(buf, 0, n);
                    if (ms.Length > 64L * 1024 * 1024)
                    {
                        throw new IOException("download too large");
                    }
                }
                return ms.ToArray();
            }
        }

        internal static string Sha256(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(data);
                StringBuilder sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
        }

        internal static bool VerifySignature(byte[] data, byte[] signature)
        {
            using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false;
                rsa.FromXmlString(PublicKeyXml);
                using (SHA256 sha = SHA256.Create())
                {
                    return rsa.VerifyData(data, sha, signature);
                }
            }
        }

        private static Version ParseVersion(string v)
        {
            try
            {
                return new Version(v);
            }
            catch
            {
                return null;
            }
        }

        private static bool SafeName(string name)
        {
            return !string.IsNullOrEmpty(name)
                && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
                && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                && name.Length <= 100;
        }

        private static void UpdateCheck()
        {
            try
            {
                Thread.Sleep(3000);
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
                byte[] manifest = Download(ManifestUrl);
                byte[] sig = Convert.FromBase64String(Encoding.ASCII.GetString(Download(SignatureUrl)).Trim());
                if (!VerifySignature(manifest, sig))
                {
                    Log("update check: manifest signature is NOT valid. Ignoring it.");
                    return;
                }

                string release = "?";
                string loaderVersion = null;
                Dictionary<string, string[]> files = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in Encoding.UTF8.GetString(manifest).Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#"))
                    {
                        continue;
                    }
                    string[] p = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p[0] == "release" && p.Length >= 2)
                    {
                        release = p[1];
                    }
                    else if (p[0] == "loader" && p.Length >= 2)
                    {
                        loaderVersion = p[1];
                    }
                    else if (p[0] == "mod" && p.Length >= 4 && SafeName(p[1]))
                    {
                        files[p[1]] = new[] { p[2].ToLowerInvariant(), p[3] };
                    }
                }

                // Never go backwards: refuse a signed but older release (replayed old manifest).
                string releaseFile = Path.Combine(modsDir, ".release");
                Version newRel = ParseVersion(release);
                Version oldRel = File.Exists(releaseFile) ? ParseVersion(File.ReadAllText(releaseFile).Trim()) : null;
                if (newRel == null || (oldRel != null && newRel < oldRel))
                {
                    Log("update check: release " + release + " is older than the installed " + oldRel + ". Ignoring it.");
                    return;
                }

                string pending = PendingDir;
                int staged = 0;
                foreach (KeyValuePair<string, string[]> f in files)
                {
                    string local = Path.Combine(modsDir, f.Key);
                    string staging = Path.Combine(pending, f.Key);
                    if (File.Exists(local) && Sha256(File.ReadAllBytes(local)) == f.Value[0])
                    {
                        continue;
                    }
                    if (File.Exists(staging) && Sha256(File.ReadAllBytes(staging)) == f.Value[0])
                    {
                        continue;
                    }
                    string url = f.Value[1];
                    if (!url.StartsWith("https://github.com/" + Repo + "/", StringComparison.Ordinal))
                    {
                        Log("update check: refusing download from unexpected address for " + f.Key);
                        continue;
                    }
                    byte[] data = Download(url);
                    if (Sha256(data) != f.Value[0])
                    {
                        Log("update check: " + f.Key + " did not match its checksum. Skipped.");
                        continue;
                    }
                    Directory.CreateDirectory(pending);
                    string tmp = staging + ".tmp";
                    File.WriteAllBytes(tmp, data);
                    File.WriteAllText(staging + ".sha256", f.Value[0]);
                    if (File.Exists(staging))
                    {
                        File.Delete(staging);
                    }
                    File.Move(tmp, staging);
                    staged++;
                    Log("downloaded update for " + f.Key);
                }

                // Mods this loader installed earlier but that are no longer in the release get removed.
                string managedList = Path.Combine(modsDir, ".managed");
                if (File.Exists(managedList))
                {
                    foreach (string old in File.ReadAllLines(managedList))
                    {
                        string name = old.Trim();
                        if (SafeName(name) && !files.ContainsKey(name) && File.Exists(Path.Combine(modsDir, name)))
                        {
                            Directory.CreateDirectory(pending);
                            File.WriteAllText(Path.Combine(pending, name + ".delete"), "");
                            staged++;
                            Log("mod " + name + " was retired; it will be removed next launch.");
                        }
                    }
                }
                File.WriteAllLines(managedList, new List<string>(files.Keys).ToArray());
                File.WriteAllText(releaseFile, release);

                if (staged > 0)
                {
                    Log("update to release " + release + " downloaded. It applies the next time you start the game.");
                }
                else
                {
                    Log("mods are up to date (release " + release + ").");
                }
                if (loaderVersion != null && loaderVersion != Version)
                {
                    Log("a new loader (v" + loaderVersion + ") is available: run the latest installer from https://github.com/" + Repo + "/releases/latest");
                }
            }
            catch (Exception e)
            {
                Log("update check failed (offline?): " + e.Message);
            }
        }
    }
}
