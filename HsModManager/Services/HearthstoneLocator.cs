using HsModManager.Models;
using Microsoft.Win32;
using System.IO;

namespace HsModManager.Services;

public static class HearthstoneLocator
{
    public static Task<List<InstallCandidate>> FindInstallationsAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var candidates = new List<InstallCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddCandidate(string? path, string source)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                string normalized;
                try
                {
                    normalized = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim('"', ' ')));
                }
                catch
                {
                    return;
                }

                if (!LooksLikeHearthstoneRoot(normalized) || !seen.Add(normalized))
                {
                    return;
                }

                candidates.Add(new InstallCandidate
                {
                    Path = normalized,
                    Source = source
                });
            }

            foreach (string path in ReadRegistryInstallLocations())
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddCandidate(path, "注册表");
            }

            foreach (string path in ReadBattleNetLauncherHints())
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddCandidate(path, "Battle.net 线索");
            }

            foreach (string path in CommonLocations())
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddCandidate(path, "常见目录");
            }

            return candidates;
        }, cancellationToken);
    }

    public static bool LooksLikeHearthstoneRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return false;
        }

        string exe = Path.Combine(path, "Hearthstone.exe");
        string data = Path.Combine(path, "Hearthstone_Data");
        string dataDir = Path.Combine(path, "Data");
        string productDb = Path.Combine(path, ".product.db");
        return File.Exists(exe) || Directory.Exists(data) || Directory.Exists(dataDir) || File.Exists(productDb);
    }

    private static IEnumerable<string> ReadRegistryInstallLocations()
    {
        var hives = new[]
        {
            RegistryHive.LocalMachine,
            RegistryHive.CurrentUser
        };

        var views = new[]
        {
            RegistryView.Registry64,
            RegistryView.Registry32
        };

        foreach (RegistryHive hive in hives)
        {
            foreach (RegistryView view in views)
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                foreach (string subPath in new[]
                {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                })
                {
                    using RegistryKey? uninstall = baseKey.OpenSubKey(subPath);
                    if (uninstall == null)
                    {
                        continue;
                    }

                    foreach (string name in uninstall.GetSubKeyNames())
                    {
                        using RegistryKey? app = uninstall.OpenSubKey(name);
                        string display = Convert.ToString(app?.GetValue("DisplayName")) ?? "";
                        if (!display.Contains("Hearthstone", StringComparison.OrdinalIgnoreCase) &&
                            !display.Contains("炉石", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string? installLocation = Convert.ToString(app?.GetValue("InstallLocation"));
                        if (!string.IsNullOrWhiteSpace(installLocation))
                        {
                            yield return installLocation;
                        }

                        string? displayIcon = Convert.ToString(app?.GetValue("DisplayIcon"));
                        if (!string.IsNullOrWhiteSpace(displayIcon))
                        {
                            string candidate = displayIcon.Trim('"');
                            if (File.Exists(candidate))
                            {
                                yield return Path.GetDirectoryName(candidate) ?? "";
                            }
                        }
                    }
                }
            }
        }
    }

    private static IEnumerable<string> ReadBattleNetLauncherHints()
    {
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string agentDir = Path.Combine(programData, "Battle.net", "Agent");
        if (!Directory.Exists(agentDir))
        {
            yield break;
        }

        foreach (string file in Directory.EnumerateFiles(agentDir, "*", SearchOption.AllDirectories).Take(80))
        {
            string name = Path.GetFileName(file);
            if (!name.Contains("product", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("config", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch
            {
                continue;
            }

            foreach (string candidate in ExtractWindowsPaths(text))
            {
                if (candidate.Contains("Hearthstone", StringComparison.OrdinalIgnoreCase))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static IEnumerable<string> ExtractWindowsPaths(string text)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(
            text,
            @"[A-Za-z]:\\(?:[^\\/:*?""<>|\r\n]+\\)*[^\\/:*?""<>|\r\n]*");

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            string path = match.Value.TrimEnd('\\');
            if (File.Exists(path))
            {
                path = Path.GetDirectoryName(path) ?? path;
            }

            yield return path;
        }
    }

    private static IEnumerable<string> CommonLocations()
    {
        var roots = new List<string?>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetEnvironmentVariable("ProgramW6432")
        };

        foreach (DriveInfo drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "Program Files (x86)"));
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "Program Files"));
            roots.Add(drive.RootDirectory.FullName);
        }

        foreach (string? root in roots.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.Combine(root!, "Hearthstone");
            yield return Path.Combine(root!, "Battle.net", "Hearthstone");
            yield return Path.Combine(root!, "Games", "Hearthstone");
        }
    }
}
