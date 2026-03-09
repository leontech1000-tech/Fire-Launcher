using System;
using System.IO;
using FireLauncher.Models;

namespace FireLauncher.Services
{
    internal sealed class LauncherSettingsService
    {
        private const string DefaultCatalogRepositoryUrl = "https://github.com/leontech1000-tech/Legacy-DB";
        private const string DefaultDiscordApplicationId = "1480364960981713108";

        public string AppDataRoot
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FireLauncher");
            }
        }

        public string SettingsPath
        {
            get { return Path.Combine(AppDataRoot, "launcher-settings.json"); }
        }

        public string DefaultProfilesRoot
        {
            get { return Path.Combine(AppDataRoot, "Profiles"); }
        }

        public string DefaultTestForksRoot
        {
            get { return Path.Combine(AppDataRoot, "TestForks"); }
        }

        public LauncherSettings Load()
        {
            var settingsExists = File.Exists(SettingsPath);
            var settings = JsonFileService.Load<LauncherSettings>(SettingsPath) ?? new LauncherSettings();

            if (string.IsNullOrWhiteSpace(settings.ProfilesRoot))
            {
                settings.ProfilesRoot = DefaultProfilesRoot;
            }

            if (string.IsNullOrWhiteSpace(settings.DownloadedForksRoot))
            {
                settings.DownloadedForksRoot = DefaultTestForksRoot;
            }

            if (string.IsNullOrWhiteSpace(settings.ForkCatalogRepositoryUrl))
            {
                settings.ForkCatalogRepositoryUrl = DefaultCatalogRepositoryUrl;
            }

            if (!settingsExists)
            {
                settings.DiscordPresenceEnabled = true;
            }
            else if (!settings.DiscordPresenceEnabled && settings.DiscordApplicationId == null)
            {
                settings.DiscordPresenceEnabled = true;
            }

            if (string.IsNullOrWhiteSpace(settings.DiscordApplicationId))
            {
                settings.DiscordApplicationId = DefaultDiscordApplicationId;
            }

            Directory.CreateDirectory(AppDataRoot);
            Directory.CreateDirectory(settings.ProfilesRoot);
            Directory.CreateDirectory(settings.DownloadedForksRoot);

            Save(settings);
            return settings;
        }

        public void Save(LauncherSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(settings.ProfilesRoot))
            {
                settings.ProfilesRoot = DefaultProfilesRoot;
            }

            if (string.IsNullOrWhiteSpace(settings.DownloadedForksRoot))
            {
                settings.DownloadedForksRoot = DefaultTestForksRoot;
            }

            if (string.IsNullOrWhiteSpace(settings.ForkCatalogRepositoryUrl))
            {
                settings.ForkCatalogRepositoryUrl = DefaultCatalogRepositoryUrl;
            }

            if (string.IsNullOrWhiteSpace(settings.DiscordApplicationId))
            {
                settings.DiscordApplicationId = DefaultDiscordApplicationId;
            }

            Directory.CreateDirectory(AppDataRoot);
            Directory.CreateDirectory(settings.ProfilesRoot);
            Directory.CreateDirectory(settings.DownloadedForksRoot);

            JsonFileService.Save(SettingsPath, settings);
        }
    }
}
