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

        public ForkProfileService(LauncherSettingsService settingsService)
        {
            _settingsService = settingsService;
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
            profile.Forks = profile.Forks ?? new List<ForkDefinition>();

            EnsureKnownForks(profile);

            if (string.IsNullOrWhiteSpace(profile.SelectedForkId))
            {
                profile.SelectedForkId = profile.Forks.FirstOrDefault(fork => fork.Enabled)?.Id
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
            EnsureKnownFork(
                profile,
                "lcemp",
                "LCEMP v1.0.3",
                "LCEMP v1.0.3",
                true,
                true,
                false,
                "-name",
                "-ip",
                string.Empty,
                "LCEMP supports -ip <host> and -name <username> launch arguments.");

            EnsureKnownFork(
                profile,
                "minecraftconsoles",
                "MinecraftConsoles",
                "LCEWindows64",
                false,
                false,
                false,
                string.Empty,
                string.Empty,
                string.Empty,
                "MinecraftConsoles launches directly from its executable. Multiplayer support is detected from the fork files.");
        }

        public string FindExecutablePath(string installDirectory)
        {
            if (string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory))
            {
                return string.Empty;
            }

            var executables = Directory.GetFiles(installDirectory, "*.exe", SearchOption.TopDirectoryOnly);
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
                SelectedForkId = "lcemp",
                Forks = new List<ForkDefinition>()
            };

            EnsureKnownForks(profile);
            return profile;
        }

        private void EnsureKnownFork(
            ForkProfile profile,
            string id,
            string displayName,
            string sourceFolderName,
            bool supportsUsernameArgument,
            bool supportsServerIpArgument,
            bool supportsPortArgument,
            string launchArgumentName,
            string launchArgumentIp,
            string launchArgumentPort,
            string notes)
        {
            var existing = profile.Forks.FirstOrDefault(fork =>
                string.Equals(fork.Id, id, StringComparison.OrdinalIgnoreCase));
            var installDirectory = ResolveKnownForkInstallDirectory(sourceFolderName);
            var executablePath = FindExecutablePath(installDirectory);

            if (existing == null)
            {
                existing = new ForkDefinition { Id = id };
                profile.Forks.Add(existing);
            }

            existing.DisplayName = displayName;
            existing.InstallDirectory = installDirectory;
            existing.ExecutablePath = executablePath;
            existing.Enabled = File.Exists(executablePath);
            existing.HasMultiplayer = DetectMultiplayerSupport(installDirectory);
            existing.SupportsUsernameArgument = supportsUsernameArgument;
            existing.SupportsServerIpArgument = supportsServerIpArgument;
            existing.SupportsPortArgument = supportsPortArgument;
            existing.LaunchArgumentName = launchArgumentName ?? string.Empty;
            existing.LaunchArgumentIp = launchArgumentIp ?? string.Empty;
            existing.LaunchArgumentPort = launchArgumentPort ?? string.Empty;
            existing.Notes = notes;
        }

        private string ResolveKnownForkInstallDirectory(string sourceFolderName)
        {
            var copiedBuild = Path.Combine(_settingsService.DefaultTestForksRoot, sourceFolderName);
            if (Directory.Exists(copiedBuild))
            {
                return copiedBuild;
            }

            var downloadsBuild = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads",
                sourceFolderName);

            return Directory.Exists(downloadsBuild) ? downloadsBuild : string.Empty;
        }

        private static bool DetectMultiplayerSupport(string installDirectory)
        {
            if (string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory))
            {
                return false;
            }

            return File.Exists(Path.Combine(installDirectory, "windows.xbox.networking.realtimesession.dll"));
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
