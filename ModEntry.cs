using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using StardewModdingAPI;

namespace ErrorPatch
{
    /// <summary>
    /// Checks every installed mod manifest at game start, downloads missing
    /// required mods listed in downloads.json, then restarts the game so the
    /// newly installed mods are loaded by SMAPI on the next launch.
    /// </summary>
    public class ModEntry : Mod
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private bool ran;

        public override void Entry(IModHelper helper)
        {
            helper.Events.GameLoop.GameLaunched += (sender, e) =>
            {
                if (ran)
                    return;
                ran = true;
                Run(helper);
            };
        }

        private void Run(IModHelper helper)
        {
            string modsDir = Directory.GetParent(helper.DirectoryPath)?.FullName ?? helper.DirectoryPath;
            var mods = ScanMods(modsDir);
            Monitor.Log($"ErrorPatch: found {mods.Count} mods.", LogLevel.Info);

            List<Problem> problems = Check(mods);
            if (problems.Count == 0)
            {
                Monitor.Log("ErrorPatch: all mod dependencies are satisfied.", LogLevel.Info);
                WriteReport(modsDir, mods.Count, problems, new List<string>());
                return;
            }

            foreach (Problem p in problems)
            {
                string text = p.Kind == "missing"
                    ? $"missing {p.Needed} (required by {p.Mod})"
                    : $"outdated {p.Needed} {p.Version}, needs {p.Detail} (required by {p.Mod})";
                Monitor.Log("ErrorPatch: " + text, LogLevel.Warn);
            }

            List<string> downloaded = DownloadMissing(helper, modsDir, problems);
            mods = ScanMods(modsDir);
            problems = Check(mods);
            WriteReport(modsDir, mods.Count, problems, downloaded);

            if (downloaded.Count > 0)
            {
                Monitor.Log("ErrorPatch: installed " + string.Join(", ", downloaded) + ". Restarting Stardew Valley so SMAPI loads them...", LogLevel.Alert);
                try
                {
                    var start = new ProcessStartInfo("cmd.exe", "/c timeout /t 2 >nul & start steam://rungameid/413150")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    Process.Start(start);
                }
                catch (Exception ex)
                {
                    Monitor.Log("ErrorPatch: could not restart automatically: " + ex.Message, LogLevel.Error);
                }
                Environment.Exit(0);
            }
        }

        private Dictionary<string, InstalledMod> ScanMods(string modsDir)
        {
            var result = new Dictionary<string, InstalledMod>(StringComparer.OrdinalIgnoreCase);
            foreach (string dir in Directory.GetDirectories(modsDir))
            {
                string path = Path.Combine(dir, "manifest.json");
                if (!File.Exists(path))
                    continue;
                try
                {
                    var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), JsonOptions);
                    if (manifest == null || string.IsNullOrWhiteSpace(manifest.UniqueID))
                        continue;
                    result[manifest.UniqueID.Trim()] = new InstalledMod
                    {
                        Manifest = manifest,
                        Folder = dir
                    };
                }
                catch
                {
                    // Ignore malformed manifests, just like SMAPI does.
                }
            }
            return result;
        }

        private List<Problem> Check(Dictionary<string, InstalledMod> mods)
        {
            var result = new List<Problem>();
            foreach (InstalledMod installed in mods.Values)
            {
                Manifest m = installed.Manifest;
                foreach (ModDependency dep in m.Dependencies ?? new List<ModDependency>())
                {
                    if (dep.IsRequired == false || string.IsNullOrWhiteSpace(dep.UniqueID))
                        continue;
                    AddDependencyProblem(result, mods, m.UniqueID, dep);
                }

                if (m.ContentPackFor != null && !string.IsNullOrWhiteSpace(m.ContentPackFor.UniqueID))
                    AddDependencyProblem(result, mods, m.UniqueID, m.ContentPackFor, "content pack framework");

                if (!string.IsNullOrWhiteSpace(m.MinimumGameVersion) && VersionLess("1.6.0", m.MinimumGameVersion))
                    result.Add(new Problem { Kind = "game", Mod = m.UniqueID, Needed = "Stardew Valley", Detail = m.MinimumGameVersion });
            }
            return result;
        }

        private static void AddDependencyProblem(List<Problem> result, Dictionary<string, InstalledMod> mods, string modID, ModDependency dep, string suffix = "")
        {
            string id = dep.UniqueID.Trim();
            if (!mods.TryGetValue(id, out InstalledMod other))
            {
                result.Add(new Problem
                {
                    Kind = "missing",
                    Mod = modID,
                    Needed = id + suffix
                });
                return;
            }

            if (!string.IsNullOrWhiteSpace(dep.MinimumVersion) && VersionLess(other.Manifest.Version, dep.MinimumVersion))
            {
                result.Add(new Problem
                {
                    Kind = "old",
                    Mod = modID,
                    Needed = id,
                    Detail = dep.MinimumVersion,
                    Version = other.Manifest.Version
                });
            }
        }

        private List<string> DownloadMissing(IModHelper helper, string modsDir, List<Problem> problems)
        {
            var urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            LoadUrls(Path.Combine(modsDir, "downloads.json"), urls);
            LoadUrls(Path.Combine(helper.DirectoryPath, "downloads.json"), urls);
            if (urls.Count == 0)
            {
                Monitor.Log("ErrorPatch: no downloads.json found, add direct mod ZIP URLs there to enable auto-download.", LogLevel.Info);
                return new List<string>();
            }

            var downloaded = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

            foreach (Problem p in problems)
            {
                string id = p.Needed.Replace(" (content pack framework)", "").Trim();
                if (!seen.Add(id))
                    continue;
                if (!urls.TryGetValue(id, out string url) || string.IsNullOrWhiteSpace(url))
                {
                    Monitor.Log($"ErrorPatch: no download URL for {id}.", LogLevel.Warn);
                    continue;
                }
                if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    Monitor.Log($"ErrorPatch: bad URL for {id}: only http/https are supported.", LogLevel.Error);
                    continue;
                }

                try
                {
                    Monitor.Log($"ErrorPatch: downloading {id} ...", LogLevel.Info);
                    byte[] data = client.GetByteArrayAsync(url).GetAwaiter().GetResult();
                    InstallDownload(modsDir, id, url, data);
                    downloaded.Add(id);
                    Monitor.Log($"ErrorPatch: installed {id}.", LogLevel.Info);
                }
                catch (Exception ex)
                {
                    Monitor.Log($"ErrorPatch: failed to install {id}: {ex.Message}", LogLevel.Error);
                }
            }
            return downloaded;
        }

        private static void LoadUrls(string path, Dictionary<string, string> urls)
        {
            if (!File.Exists(path))
                return;
            try
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), JsonOptions);
                if (loaded == null)
                    return;
                foreach (KeyValuePair<string, string> pair in loaded)
                    urls[pair.Key] = pair.Value;
            }
            catch (Exception ex)
            {
                // Reported by caller only indirectly; keep going with whatever loaded.
                Console.Error.WriteLine("ErrorPatch: bad downloads.json: " + ex.Message);
            }
        }

        private static void InstallDownload(string modsDir, string id, string url, byte[] data)
        {
            string name = Path.GetFileName(new Uri(url).GetLeftPart(UriPartial.Path));
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                string tmp = Path.Combine(Path.GetTempPath(), "errorpatch-" + Guid.NewGuid().ToString("N") + ".zip");
                File.WriteAllBytes(tmp, data);
                try
                {
                    using var archive = ZipFile.OpenRead(tmp);
                    string root = Path.GetFullPath(modsDir);
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        string full = Path.GetFullPath(Path.Combine(root, entry.FullName));
                        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal))
                        {
                            Directory.CreateDirectory(full);
                            continue;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(full));
                        entry.ExtractToFile(full, true);
                    }
                }
                finally
                {
                    File.Delete(tmp);
                }
                return;
            }

            string target = Path.Combine(modsDir, id);
            Directory.CreateDirectory(target);
            File.WriteAllBytes(Path.Combine(target, string.IsNullOrWhiteSpace(name) ? id + ".dll" : name), data);
        }

        private void WriteReport(string modsDir, int modCount, List<Problem> problems, List<string> downloaded)
        {
            try
            {
                var lines = new List<string>
                {
                    "ErrorPatch report - " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    "Mods: " + modCount,
                    "Downloaded: " + (downloaded.Count == 0 ? "none" : string.Join(", ", downloaded)),
                    ""
                };
                if (problems.Count == 0)
                    lines.Add("OK: all dependencies satisfied.");
                else
                {
                    lines.Add("Remaining problems: " + problems.Count);
                    foreach (Problem p in problems)
                        lines.Add("- " + p.Mod + " needs " + p.Needed + " " + p.Detail);
                }
                File.WriteAllLines(Path.Combine(modsDir, "errorpatch-report.txt"), lines);
            }
            catch (Exception ex)
            {
                Monitor.Log("ErrorPatch: could not write report: " + ex.Message, LogLevel.Warn);
            }
        }

        private static bool VersionLess(string a, string b)
        {
            List<int> pa = Numbers(a);
            List<int> pb = Numbers(b);
            int n = Math.Max(pa.Count, pb.Count);
            for (int i = 0; i < n; i++)
            {
                int x = i < pa.Count ? pa[i] : 0;
                int y = i < pb.Count ? pb[i] : 0;
                if (x < y) return true;
                if (x > y) return false;
            }
            return false;
        }

        private static List<int> Numbers(string s)
        {
            var result = new List<int>();
            string current = "";
            foreach (char c in s ?? "")
            {
                if (char.IsDigit(c))
                    current += c;
                else if (current.Length > 0)
                {
                    result.Add(int.Parse(current));
                    current = "";
                }
            }
            if (current.Length > 0)
                result.Add(int.Parse(current));
            return result;
        }

        private sealed class InstalledMod
        {
            public Manifest Manifest { get; set; }
            public string Folder { get; set; }
        }

        private sealed class Problem
        {
            public string Kind { get; set; }
            public string Mod { get; set; }
            public string Needed { get; set; }
            public string Detail { get; set; }
            public string Version { get; set; }
        }

        private sealed class Manifest
        {
            [JsonPropertyName("Name")] public string Name { get; set; }
            [JsonPropertyName("UniqueID")] public string UniqueID { get; set; }
            [JsonPropertyName("Version")] public string Version { get; set; }
            [JsonPropertyName("MinimumGameVersion")] public string MinimumGameVersion { get; set; }
            [JsonPropertyName("Dependencies")] public List<ModDependency> Dependencies { get; set; }
            [JsonPropertyName("ContentPackFor")] public ModDependency ContentPackFor { get; set; }
        }

        private sealed class ModDependency
        {
            [JsonPropertyName("UniqueID")] public string UniqueID { get; set; }
            [JsonPropertyName("MinimumVersion")] public string MinimumVersion { get;set; }
            [JsonPropertyName("IsRequired")] public bool? IsRequired { get; set; }
        }
    }
}
