using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using FireLauncher.Interop;
using FireLauncher.Models;
using FireLauncher.Services;
using Forms = System.Windows.Forms;

namespace FireLauncher
{
    public partial class MainWindow : Window
    {
        private readonly LauncherSettingsService _settingsService;
        private readonly ForkProfileService _profileService;
        private readonly DiscordPresenceService _discordPresenceService;

        private LauncherSettings _settings;
        private ForkProfile _currentProfile;
        private bool _suppressUiEvents;

        public MainWindow()
        {
            InitializeComponent();

            WindowBlur.TryEnable(this);

            _settingsService = new LauncherSettingsService();
            _profileService = new ForkProfileService(_settingsService);
            _discordPresenceService = new DiscordPresenceService();

            LoadApplicationState();
        }

        protected override void OnClosed(EventArgs e)
        {
            SaveCurrentProfileState();
            _discordPresenceService.Dispose();
            base.OnClosed(e);
        }

        private void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new Forms.FolderBrowserDialog())
            {
                dialog.Description = "Choose a folder for Fire Launcher profiles";
                dialog.SelectedPath = ProfilePathTextBox.Text;
                dialog.ShowNewFolderButton = true;

                if (dialog.ShowDialog() == Forms.DialogResult.OK)
                {
                    ProfilePathTextBox.Text = dialog.SelectedPath;
                }
            }
        }

        private void OpenProfiles_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null)
            {
                return;
            }

            Directory.CreateDirectory(_settings.ProfilesRoot);
            Process.Start("explorer.exe", _settings.ProfilesRoot);
        }

        private void LaunchButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProfile == null)
            {
                MessageBox.Show(
                    "No profile is loaded yet.",
                    "Fire Launcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            SaveCurrentProfileState();

            var selectedFork = ForkComboBox.SelectedItem as ForkDefinition;
            if (selectedFork == null)
            {
                MessageBox.Show(
                    "No installed fork is available to launch.",
                    "Fire Launcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var port = _currentProfile.DefaultServerPort;
            if (selectedFork.SupportsPortArgument)
            {
                if (!int.TryParse(PortTextBox.Text, out port) || port < 1 || port > 65535)
                {
                    MessageBox.Show(
                        "Enter a valid port between 1 and 65535.",
                        "Fire Launcher",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(selectedFork.ExecutablePath) || !File.Exists(selectedFork.ExecutablePath))
            {
                MessageBox.Show(
                    "The selected fork executable was not found.",
                    "Fire Launcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var arguments = BuildLaunchArguments(selectedFork, UsernameTextBox.Text, ServerIpTextBox.Text, port);
            var startInfo = new ProcessStartInfo
            {
                FileName = selectedFork.ExecutablePath,
                WorkingDirectory = Path.GetDirectoryName(selectedFork.ExecutablePath),
                Arguments = arguments,
                UseShellExecute = false
            };

            Process.Start(startInfo);
            UpdateDiscordPresence("Launching " + selectedFork.DisplayName, _currentProfile.DisplayName);
        }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null)
            {
                return;
            }

            SaveCurrentProfileState();

            _settings.ProfilesRoot = string.IsNullOrWhiteSpace(ProfilePathTextBox.Text)
                ? _settingsService.DefaultProfilesRoot
                : ProfilePathTextBox.Text.Trim();
            _settings.DiscordPresenceEnabled = DiscordPresenceCheckBox.IsChecked == true;
            _settings.DiscordApplicationId = DiscordApplicationIdTextBox.Text == null
                ? string.Empty
                : DiscordApplicationIdTextBox.Text.Trim();

            _settingsService.Save(_settings);
            _profileService.EnsureSeedData(_settings);
            ConfigureDiscordPresence();
            LoadProfilesIntoUi();

            MessageBox.Show(
                "Settings saved.",
                "Fire Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void ProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressUiEvents)
            {
                return;
            }

            SaveCurrentProfileState();
            LoadSelectedProfile();
        }

        private void ForkComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressUiEvents || _currentProfile == null)
            {
                return;
            }

            var selectedFork = ForkComboBox.SelectedItem as ForkDefinition;
            _currentProfile.SelectedForkId = selectedFork == null ? null : selectedFork.Id;

            SyncForkListSelection(_currentProfile.SelectedForkId);
            UpdateLaunchSummary(selectedFork);
            UpdateLaunchInputs(selectedFork);
            UpdateDiscordPresence(
                selectedFork == null ? "Editing launcher" : "Editing " + selectedFork.DisplayName,
                _currentProfile.DisplayName);
        }

        private void ForkListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressUiEvents || _currentProfile == null)
            {
                return;
            }

            var selectedFork = ForkListBox.SelectedItem as ForkDefinition;
            PopulateForkDetails(selectedFork);

            if (selectedFork == null)
            {
                return;
            }

            _currentProfile.SelectedForkId = selectedFork.Id;

            if (selectedFork.Enabled)
            {
                SyncLauncherForkSelection(selectedFork.Id);
                UpdateLaunchSummary(selectedFork);
            }
        }

        private void RefreshForks_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProfile == null)
            {
                return;
            }

            SaveCurrentProfileState();
            _profileService.RefreshKnownForks(_currentProfile);
            _profileService.SaveProfile(_settings.ProfilesRoot, _currentProfile);
            PopulateProfileUi(_currentProfile);

            MessageBox.Show(
                "Fork records refreshed from local installs.",
                "Fire Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void OpenForkFolder_Click(object sender, RoutedEventArgs e)
        {
            var fork = GetSelectedForkForDetails();
            if (fork == null || string.IsNullOrWhiteSpace(fork.InstallDirectory) || !Directory.Exists(fork.InstallDirectory))
            {
                MessageBox.Show(
                    "This fork does not have a valid install directory yet.",
                    "Fire Launcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            Process.Start("explorer.exe", fork.InstallDirectory);
        }

        private void LoadApplicationState()
        {
            _settings = _settingsService.Load();
            _profileService.EnsureSeedData(_settings);

            ProfilePathTextBox.Text = _settings.ProfilesRoot;
            DiscordPresenceCheckBox.IsChecked = _settings.DiscordPresenceEnabled;
            DiscordApplicationIdTextBox.Text = _settings.DiscordApplicationId ?? string.Empty;

            LoadProfilesIntoUi();
            ConfigureDiscordPresence();
        }

        private void LoadProfilesIntoUi()
        {
            _suppressUiEvents = true;

            var profiles = _profileService.ListProfileIds(_settings.ProfilesRoot).ToList();
            ProfileComboBox.ItemsSource = profiles;

            var selectedProfileId = _settings.SelectedProfileId;
            if (string.IsNullOrWhiteSpace(selectedProfileId) || !profiles.Contains(selectedProfileId))
            {
                selectedProfileId = profiles.FirstOrDefault();
            }

            ProfileComboBox.SelectedItem = selectedProfileId;
            _suppressUiEvents = false;

            LoadSelectedProfile();
        }

        private void LoadSelectedProfile()
        {
            var selectedProfileId = ProfileComboBox.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(selectedProfileId))
            {
                return;
            }

            _currentProfile = _profileService.LoadProfile(_settings.ProfilesRoot, selectedProfileId);
            _settings.SelectedProfileId = _currentProfile.Id;
            _settingsService.Save(_settings);

            PopulateProfileUi(_currentProfile);
            UpdateDiscordPresence("Editing profile", _currentProfile.DisplayName);
        }

        private void PopulateProfileUi(ForkProfile profile)
        {
            if (profile == null)
            {
                return;
            }

            _suppressUiEvents = true;

            UsernameTextBox.Text = profile.DefaultUsername ?? string.Empty;
            ServerIpTextBox.Text = profile.DefaultServerIp ?? string.Empty;
            PortTextBox.Text = profile.DefaultServerPort.ToString();
            SelectedProfileSummaryText.Text = profile.DisplayName;
            ProfileDatabasePathText.Text = _profileService.GetForkDatabasePath(_settings.ProfilesRoot, profile.Id);
            ProfileDbSummaryText.Text = "Fork DB: " + _profileService.GetForkDatabasePath(_settings.ProfilesRoot, profile.Id);

            var allForks = profile.Forks ?? new List<ForkDefinition>();
            var enabledForks = allForks.Where(fork => fork.Enabled).ToList();

            ForkListBox.ItemsSource = allForks;
            ForkComboBox.ItemsSource = enabledForks;

            var selectedDetailsFork = allForks.FirstOrDefault(fork => fork.Id == profile.SelectedForkId)
                ?? allForks.FirstOrDefault();
            var selectedLaunchFork = enabledForks.FirstOrDefault(fork => fork.Id == profile.SelectedForkId)
                ?? enabledForks.FirstOrDefault();

            ForkListBox.SelectedItem = selectedDetailsFork;
            ForkComboBox.SelectedItem = selectedLaunchFork;

            _suppressUiEvents = false;

            PopulateForkDetails(selectedDetailsFork);
            UpdateLaunchSummary(selectedLaunchFork);
            UpdateLaunchInputs(selectedLaunchFork);
            UpdateDetectedForksSummary(allForks);
        }

        private void PopulateForkDetails(ForkDefinition fork)
        {
            if (fork == null)
            {
                ForkDetailsTitleText.Text = "Fork details";
                ForkInstallStatusText.Text = "No fork selected.";
                ForkMultiplayerText.Text = string.Empty;
                ForkArgumentsText.Text = string.Empty;
                ForkExecutablePathText.Text = string.Empty;
                ForkInstallPathText.Text = string.Empty;
                ForkNotesText.Text = string.Empty;
                return;
            }

            ForkDetailsTitleText.Text = fork.DisplayName;
            ForkInstallStatusText.Text = fork.Enabled ? "Installed and launchable" : "Not installed";
            ForkMultiplayerText.Text = fork.HasMultiplayer ? "Yes" : "No";
            ForkArgumentsText.Text = BuildArgumentsDescription(fork);
            ForkExecutablePathText.Text = string.IsNullOrWhiteSpace(fork.ExecutablePath) ? "Not found" : fork.ExecutablePath;
            ForkInstallPathText.Text = string.IsNullOrWhiteSpace(fork.InstallDirectory) ? "Not found" : fork.InstallDirectory;
            ForkNotesText.Text = string.IsNullOrWhiteSpace(fork.Notes) ? "No notes." : fork.Notes;
        }

        private void SaveCurrentProfileState()
        {
            if (_currentProfile == null || _settings == null)
            {
                return;
            }

            _currentProfile.DefaultUsername = UsernameTextBox.Text == null ? string.Empty : UsernameTextBox.Text.Trim();
            _currentProfile.DefaultServerIp = ServerIpTextBox.Text == null ? string.Empty : ServerIpTextBox.Text.Trim();
            _currentProfile.DefaultServerPort = int.TryParse(PortTextBox.Text, out var port) ? port : 19132;

            var selectedFork = ForkComboBox.SelectedItem as ForkDefinition
                ?? ForkListBox.SelectedItem as ForkDefinition;
            _currentProfile.SelectedForkId = selectedFork == null ? _currentProfile.SelectedForkId : selectedFork.Id;

            _profileService.SaveProfile(_settings.ProfilesRoot, _currentProfile);
        }

        private void SyncForkListSelection(string forkId)
        {
            if (string.IsNullOrWhiteSpace(forkId) || _currentProfile == null)
            {
                return;
            }

            _suppressUiEvents = true;
            var fork = _currentProfile.Forks.FirstOrDefault(item => item.Id == forkId);
            if (fork != null)
            {
                ForkListBox.SelectedItem = fork;
                PopulateForkDetails(fork);
            }
            _suppressUiEvents = false;
        }

        private void SyncLauncherForkSelection(string forkId)
        {
            if (string.IsNullOrWhiteSpace(forkId) || _currentProfile == null)
            {
                return;
            }

            _suppressUiEvents = true;
            var fork = _currentProfile.Forks.FirstOrDefault(item => item.Id == forkId && item.Enabled);
            if (fork != null)
            {
                ForkComboBox.SelectedItem = fork;
            }
            _suppressUiEvents = false;
        }

        private void UpdateLaunchSummary(ForkDefinition fork)
        {
            if (fork == null)
            {
                SelectedForkSummaryText.Text = "No installed fork";
                ForkCapabilitySummaryText.Text = "Refresh forks after adding a local build.";
                return;
            }

            SelectedForkSummaryText.Text = fork.DisplayName;
            ForkCapabilitySummaryText.Text = BuildLaunchSummaryText(fork);
        }

        private void UpdateLaunchInputs(ForkDefinition fork)
        {
            if (fork == null)
            {
                UsernameTextBox.IsEnabled = false;
                ServerIpTextBox.IsEnabled = false;
                PortTextBox.IsEnabled = false;
                UsernameFieldPanel.Visibility = Visibility.Visible;
                ServerIpFieldPanel.Visibility = Visibility.Visible;
                PortFieldPanel.Visibility = Visibility.Visible;
                LaunchArgsHintText.Text = "No launchable fork is selected.";
                return;
            }

            UsernameTextBox.IsEnabled = fork.SupportsUsernameArgument;
            ServerIpTextBox.IsEnabled = fork.SupportsServerIpArgument;
            PortTextBox.IsEnabled = fork.SupportsPortArgument;
            UsernameFieldPanel.Visibility = fork.SupportsUsernameArgument ? Visibility.Visible : Visibility.Collapsed;
            ServerIpFieldPanel.Visibility = fork.SupportsServerIpArgument ? Visibility.Visible : Visibility.Collapsed;
            PortFieldPanel.Visibility = fork.SupportsPortArgument ? Visibility.Visible : Visibility.Collapsed;

            if (!fork.SupportsUsernameArgument && !fork.SupportsServerIpArgument && !fork.SupportsPortArgument)
            {
                LaunchArgsHintText.Text = fork.DisplayName + " launches directly from its executable and does not use launcher arguments.";
                return;
            }

            if (fork.SupportsUsernameArgument && fork.SupportsServerIpArgument && !fork.SupportsPortArgument)
            {
                LaunchArgsHintText.Text = fork.DisplayName + " uses " + fork.LaunchArgumentName + " and " + fork.LaunchArgumentIp + ". The port is stored but not passed.";
                return;
            }

            LaunchArgsHintText.Text = BuildArgumentsDescription(fork);
        }

        private void UpdateDetectedForksSummary(IEnumerable<ForkDefinition> forks)
        {
            var installed = (forks ?? Enumerable.Empty<ForkDefinition>())
                .Where(fork => fork.Enabled)
                .Select(fork => fork.DisplayName)
                .ToList();

            DetectedForksText.Text = installed.Count == 0
                ? "No local forks detected yet."
                : "Detected: " + string.Join(", ", installed);
        }

        private static string BuildLaunchSummaryText(ForkDefinition fork)
        {
            var parts = new List<string>();

            parts.Add(fork.Enabled ? "Installed" : "Missing");
            parts.Add(fork.HasMultiplayer ? "multiplayer available" : "multiplayer unavailable");

            var args = new List<string>();
            if (fork.SupportsUsernameArgument && !string.IsNullOrWhiteSpace(fork.LaunchArgumentName))
            {
                args.Add(fork.LaunchArgumentName);
            }

            if (fork.SupportsServerIpArgument && !string.IsNullOrWhiteSpace(fork.LaunchArgumentIp))
            {
                args.Add(fork.LaunchArgumentIp);
            }

            if (fork.SupportsPortArgument && !string.IsNullOrWhiteSpace(fork.LaunchArgumentPort))
            {
                args.Add(fork.LaunchArgumentPort);
            }

            parts.Add(args.Count == 0 ? "launches directly" : "launch args: " + string.Join(", ", args));
            return string.Join(" | ", parts);
        }

        private static string BuildArgumentsDescription(ForkDefinition fork)
        {
            var args = new List<string>();

            if (fork.SupportsUsernameArgument && !string.IsNullOrWhiteSpace(fork.LaunchArgumentName))
            {
                args.Add(fork.LaunchArgumentName + " <username>");
            }

            if (fork.SupportsServerIpArgument && !string.IsNullOrWhiteSpace(fork.LaunchArgumentIp))
            {
                args.Add(fork.LaunchArgumentIp + " <server>");
            }

            if (fork.SupportsPortArgument && !string.IsNullOrWhiteSpace(fork.LaunchArgumentPort))
            {
                args.Add(fork.LaunchArgumentPort + " <port>");
            }

            return args.Count == 0 ? "No launch arguments" : string.Join(", ", args);
        }

        private static string BuildLaunchArguments(ForkDefinition fork, string username, string serverIp, int port)
        {
            var arguments = new List<string>();

            if (fork.SupportsUsernameArgument && !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(fork.LaunchArgumentName))
            {
                arguments.Add(fork.LaunchArgumentName);
                arguments.Add(QuoteArgument(username.Trim()));
            }

            if (fork.HasMultiplayer && fork.SupportsServerIpArgument && !string.IsNullOrWhiteSpace(serverIp) && !string.IsNullOrWhiteSpace(fork.LaunchArgumentIp))
            {
                arguments.Add(fork.LaunchArgumentIp);
                arguments.Add(QuoteArgument(serverIp.Trim()));
            }

            if (fork.SupportsPortArgument && !string.IsNullOrWhiteSpace(fork.LaunchArgumentPort))
            {
                arguments.Add(fork.LaunchArgumentPort);
                arguments.Add(port.ToString());
            }

            return string.Join(" ", arguments);
        }

        private ForkDefinition GetSelectedForkForDetails()
        {
            return ForkListBox.SelectedItem as ForkDefinition
                ?? ForkComboBox.SelectedItem as ForkDefinition;
        }

        private static string QuoteArgument(string value)
        {
            return value.Contains(" ")
                ? "\"" + value.Replace("\"", "\\\"") + "\""
                : value;
        }

        private void ConfigureDiscordPresence()
        {
            if (_settings == null)
            {
                return;
            }

            _discordPresenceService.Configure(
                _settings.DiscordPresenceEnabled,
                _settings.DiscordApplicationId);
        }

        private void UpdateDiscordPresence(string details, string state)
        {
            _discordPresenceService.UpdatePresence(details, state);
        }
    }
}
