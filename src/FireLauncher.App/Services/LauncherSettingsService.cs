using System;
using System.IO;
using FireLauncher.Models;

namespace FireLauncher.Services
{
    internal sealed class LauncherSettingsService
    {
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

            if (!settingsExists)
            {
                settings.DiscordPresenceEnabled = true;
            }
            else if (!settings.DiscordPresenceEnabled && settings.DiscordApplicationId == null)
            {
                settings.DiscordPresenceEnabled = true;
            }

            Directory.CreateDirectory(AppDataRoot);
            Directory.CreateDirectory(settings.ProfilesRoot);
            Directory.CreateDirectory(DefaultTestForksRoot);

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

            Directory.CreateDirectory(AppDataRoot);
            Directory.CreateDirectory(settings.ProfilesRoot);
            Directory.CreateDirectory(DefaultTestForksRoot);

            JsonFileService.Save(SettingsPath, settings);
        }
    }
}
