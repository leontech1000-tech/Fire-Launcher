using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FireLauncher.Models;

namespace FireLauncher.Services
{
    internal sealed class ForkProfileService
    {
        private readonly LauncherSettingsService _settingsService;
        private readonly ForkCatalogService _catalogService;

        public ForkProfileService(LauncherSettingsService settingsService, ForkCatalogService catalogService)
        {
            _settingsService = settingsService;
            _catalogService = catalogService;
        }

        public IEnumerable<string> ListProfileIds(string profilesRoot)
        {
            if (!Directory.Exists(profilesRoot))
            {
                return Enumerable.Empty<string>();
            }

            return Directory.GetDirectories(profilesRoot)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name);
        }

        public string GetForkDatabasePath(string profilesRoot, string profileId)
        {
            return Path.Combine(profilesRoot, profileId, "forkdb.json");
        }

        public void EnsureSeedData(LauncherSettings settings)
        {
            Directory.CreateDirectory(settings.ProfilesRoot);

            var profiles = ListProfileIds(settings.ProfilesRoot).ToList();
            if (profiles.Count == 0)
            {
                var defaultProfile = CreateDefaultProfile("Default");
                SaveProfile(settings.ProfilesRoot, defaultProfile);
                profiles.Add(defaultProfile.Id);
            }

            if (string.IsNullOrWhiteSpace(settings.SelectedProfileId) || !profiles.Contains(settings.SelectedProfileId))
            {
                settings.SelectedProfileId = profiles.First();
                _settingsService.Save(settings);
            }
        }

        public ForkProfile LoadProfile(string profilesRoot, string profileId)
        {
            var path = GetForkDatabasePath(profilesRoot, profileId);
            var profile = JsonFileService.Load<ForkProfile>(path) ?? CreateDefaultProfile(profileId);

            profile.Id = string.IsNullOrWhiteSpace(profile.Id) ? profileId : profile.Id;
            profile.DisplayName = string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.Id : profile.DisplayName;
            profile.DefaultUsername = string.IsNullOrWhiteSpace(profile.DefaultUsername)
                ? Environment.UserName
                : profile.DefaultUsername;
            profile.DefaultServerIp = profile.DefaultServerIp ?? string.Empty;
            if (string.Equals(profile.DefaultServerIp, "127.0.0.1", StringComparison.Ordinal))
            {
                profile.DefaultServerIp = string.Empty;
            }

            profile.DefaultServerPort = profile.DefaultServerPort <= 0 ? 19132 : profile.DefaultServerPort;
            if (!profile.OnlineModeEnabled && !string.IsNullOrWhiteSpace(profile.DefaultServerIp))
            {
                profile.OnlineModeEnabled = true;
            }

            profile.Forks = profile.Forks ?? new List<ForkDefinition>();

            EnsureKnownForks(profile);

            if (string.IsNullOrWhiteSpace(profile.SelectedForkId))
            {
                profile.SelectedForkId = profile.Forks.FirstOrDefault(fork => fork.Enabled && fork.ShowInLauncher)?.Id
                    ?? profile.Forks.FirstOrDefault(fork => fork.Enabled)?.Id
                    ?? profile.Forks.FirstOrDefault()?.Id;
            }

            SaveProfile(profilesRoot, profile);
            return profile;
        }

        public void SaveProfile(string profilesRoot, ForkProfile profile)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.Id))
            {
                return;
            }

            var path = GetForkDatabasePath(profilesRoot, profile.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            JsonFileService.Save(path, profile);
        }

        public void RefreshKnownForks(ForkProfile profile)
        {
            EnsureKnownForks(profile);
        }

        public void EnsureKnownForks(ForkProfile profile)
        {
            var settings = _settingsService.Load();
            var manifest = _catalogService.LoadCatalog(settings);
            var families = manifest == null ? new List<ForkCatalogFamily>() : (manifest.Forks ?? new List<ForkCatalogFamily>());
            var validForkIds = new HashSet<string>(
                families.SelectMany(family => family.Versions ?? new List<ForkCatalogVersion>())
                    .Select(version => version.Id),
                StringComparer.OrdinalIgnoreCase);

            profile.Forks.RemoveAll(fork => fork == null || string.IsNullOrWhiteSpace(fork.Id) || !validForkIds.Contains(fork.Id));

            foreach (var family in families)
            {
                var versions = family.Versions ?? new List<ForkCatalogVersion>();
                foreach (var version in versions)
                {
                    EnsureCatalogFork(profile, settings, family, version);
                }
            }

            if (!string.IsNullOrWhiteSpace(profile.SelectedForkId) &&
                profile.Forks.All(fork => !string.Equals(fork.Id, profile.SelectedForkId, StringComparison.OrdinalIgnoreCase)))
            {
                profile.SelectedForkId = null;
            }
        }

        public string FindExecutablePath(string installDirectory, string relativeExecutablePath = null)
        {
            if (string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory))
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(relativeExecutablePath))
            {
                var explicitPath = Path.Combine(installDirectory, relativeExecutablePath);
                if (File.Exists(explicitPath))
                {
                    return explicitPath;
                }
            }

            var executables = Directory.GetFiles(installDirectory, "*.exe", SearchOption.AllDirectories);
            var minecraftExecutable = executables.FirstOrDefault(path =>
                Path.GetFileName(path).StartsWith("Minecraft", StringComparison.OrdinalIgnoreCase));

            return minecraftExecutable ?? executables.FirstOrDefault() ?? string.Empty;
        }

        private ForkProfile CreateDefaultProfile(string profileId)
        {
            var profile = new ForkProfile
            {
                Id = profileId,
                DisplayName = profileId,
                DefaultUsername = Environment.UserName,
                DefaultServerIp = string.Empty,
                DefaultServerPort = 19132,
                OnlineModeEnabled = false,
                SelectedForkId = "lcemp-v1-0-3",
                Forks = new List<ForkDefinition>()
            };

            EnsureKnownForks(profile);
            return profile;
        }

        private void EnsureCatalogFork(
            ForkProfile profile,
            LauncherSettings settings,
            ForkCatalogFamily family,
            ForkCatalogVersion version)
        {
            var existing = profile.Forks.FirstOrDefault(fork =>
                string.Equals(fork.Id, version.Id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fork.Id, family.FamilyId, StringComparison.OrdinalIgnoreCase));
            var isLegacyEntry = existing != null && string.IsNullOrWhiteSpace(existing.FamilyId);
            var installDirectory = ResolveCatalogInstallDirectory(settings, version);
            var executablePath = FindExecutablePath(installDirectory, version.ExecutableRelativePath);

            if (existing == null)
            {
                existing = new ForkDefinition();
                profile.Forks.Add(existing);
                isLegacyEntry = true;
            }

            if (string.Equals(profile.SelectedForkId, family.FamilyId, StringComparison.OrdinalIgnoreCase))
            {
                profile.SelectedForkId = version.Id;
            }

            existing.Id = version.Id;
            existing.FamilyId = family.FamilyId;
            existing.FamilyName = family.FamilyName;
            existing.VersionLabel = version.VersionLabel;
            existing.DisplayName = version.DisplayName;
            existing.SourceFolderName = version.SourceFolderName;
            existing.InstallFolderName = version.InstallFolderName;
            existing.InstallDirectory = installDirectory;
            existing.ExecutablePath = executablePath;
            existing.ExecutableRelativePath = version.ExecutableRelativePath;
            existing.Enabled = File.Exists(executablePath);
            existing.HasMultiplayer = version.HasMultiplayer;
            existing.SupportsUsernameArgument = version.SupportsUsernameArgument;
            existing.SupportsServerIpArgument = version.SupportsServerIpArgument;
            existing.SupportsPortArgument = version.SupportsPortArgument;
            existing.LaunchArgumentName = version.LaunchArgumentName ?? string.Empty;
            existing.LaunchArgumentIp = version.LaunchArgumentIp ?? string.Empty;
            existing.LaunchArgumentPort = version.LaunchArgumentPort ?? string.Empty;
            existing.PackageType = version.PackageType ?? string.Empty;
            existing.PackageUrl = version.PackageUrl ?? string.Empty;
            existing.ReleaseTag = version.ReleaseTag ?? string.Empty;
            existing.AssetFileName = version.AssetFileName ?? string.Empty;
            existing.Notes = string.IsNullOrWhiteSpace(version.Notes) ? family.Notes : version.Notes;

            if (isLegacyEntry)
            {
                existing.ShowInLauncher = family.ShowInLauncherByDefault;
            }
        }

        private string ResolveCatalogInstallDirectory(LauncherSettings settings, ForkCatalogVersion version)
        {
            var stagedRoot = string.IsNullOrWhiteSpace(settings.DownloadedForksRoot)
                ? _settingsService.DefaultTestForksRoot
                : settings.DownloadedForksRoot;
            var installFolderName = string.IsNullOrWhiteSpace(version.InstallFolderName)
                ? version.Id
                : version.InstallFolderName;
            var sourceFolderName = string.IsNullOrWhiteSpace(version.SourceFolderName)
                ? installFolderName
                : version.SourceFolderName;
            var stagedBuild = Path.Combine(stagedRoot, installFolderName);
            var downloadsBuild = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads",
                sourceFolderName);

            Directory.CreateDirectory(stagedRoot);

            if (Directory.Exists(downloadsBuild))
            {
                var sourcePath = Path.GetFullPath(downloadsBuild).TrimEnd(Path.DirectorySeparatorChar);
                var targetPath = Path.GetFullPath(stagedBuild).TrimEnd(Path.DirectorySeparatorChar);

                if (!string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        ReplaceDirectory(downloadsBuild, stagedBuild);
                    }
                    catch
                    {
                        return downloadsBuild;
                    }
                }

                if (!string.IsNullOrWhiteSpace(FindExecutablePath(stagedBuild, version.ExecutableRelativePath)))
                {
                    return stagedBuild;
                }

                return !string.IsNullOrWhiteSpace(FindExecutablePath(downloadsBuild, version.ExecutableRelativePath))
                    ? downloadsBuild
                    : (Directory.Exists(stagedBuild) ? stagedBuild : string.Empty);
            }

            return !string.IsNullOrWhiteSpace(FindExecutablePath(stagedBuild, version.ExecutableRelativePath))
                ? stagedBuild
                : string.Empty;
        }

        private static void ReplaceDirectory(string sourceDirectory, string destinationDirectory)
        {
            if (Directory.Exists(destinationDirectory))
            {
                Directory.Delete(destinationDirectory, true);
            }

            CopyDirectory(sourceDirectory, destinationDirectory);
        }

        private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
        {
            Directory.CreateDirectory(destinationDirectory);

            foreach (var file in Directory.GetFiles(sourceDirectory))
            {
                var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(file));
                File.Copy(file, destinationPath, true);
            }

            foreach (var directory in Directory.GetDirectories(sourceDirectory))
            {
                var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(directory));
                CopyDirectory(directory, destinationPath);
            }
        }
    }
}
