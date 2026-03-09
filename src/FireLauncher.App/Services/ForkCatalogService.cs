using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using FireLauncher.Models;

namespace FireLauncher.Services
{
    internal sealed class ForkCatalogService
    {
        private const string DefaultCatalogRepository = "https://github.com/leontech1000-tech/Legacy-DB";
        private const string DefaultCatalogFileName = "catalog.json";

        private readonly LauncherSettingsService _settingsService;
        private readonly HttpClient _httpClient;

        public ForkCatalogService(LauncherSettingsService settingsService)
        {
            _settingsService = settingsService;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FireLauncher", "1.0"));
        }

        public string DefaultRepositoryUrl
        {
            get { return DefaultCatalogRepository; }
        }

        public string CatalogCachePath
        {
            get { return Path.Combine(_settingsService.AppDataRoot, "catalog-cache.json"); }
        }

        public ForkCatalogManifest LoadCachedCatalog()
        {
            return JsonFileService.Load<ForkCatalogManifest>(CatalogCachePath);
        }

        public ForkCatalogManifest LoadCatalog(LauncherSettings settings)
        {
            var manifest = LoadCachedCatalog();
            return manifest ?? CreateFallbackCatalog();
        }

        public ForkCatalogManifest SyncCatalog(LauncherSettings settings)
        {
            try
            {
                return SyncCatalogAsync(settings).GetAwaiter().GetResult();
            }
            catch
            {
                return LoadCatalog(settings);
            }
        }

        public async Task<ForkCatalogManifest> SyncCatalogAsync(LauncherSettings settings)
        {
            var repositoryUrl = NormalizeRepositoryUrl(settings == null ? null : settings.ForkCatalogRepositoryUrl);
            var catalogUrl = BuildCatalogUrl(repositoryUrl);
            var response = await _httpClient.GetAsync(catalogUrl).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var manifest = Deserialize<ForkCatalogManifest>(json) ?? CreateFallbackCatalog();
            JsonFileService.Save(CatalogCachePath, manifest);
            return manifest;
        }

        public string NormalizeRepositoryUrl(string repositoryUrl)
        {
            return string.IsNullOrWhiteSpace(repositoryUrl)
                ? DefaultCatalogRepository
                : repositoryUrl.Trim().TrimEnd('/');
        }

        public string BuildCatalogUrl(string repositoryUrl)
        {
            if (string.IsNullOrWhiteSpace(repositoryUrl))
            {
                return BuildCatalogUrl(DefaultCatalogRepository);
            }

            if (repositoryUrl.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                return repositoryUrl;
            }

            GitHubRepositoryCoordinates coordinates;
            if (!TryParseRepositoryUrl(repositoryUrl, out coordinates))
            {
                return repositoryUrl.TrimEnd('/') + "/" + DefaultCatalogFileName;
            }

            return "https://raw.githubusercontent.com/"
                + coordinates.Owner
                + "/"
                + coordinates.Repository
                + "/main/"
                + DefaultCatalogFileName;
        }

        public string BuildPackageUrl(LauncherSettings settings, ForkDefinition fork)
        {
            if (fork == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(fork.PackageUrl))
            {
                if (string.Equals(fork.PackageType, "repo-folder", StringComparison.OrdinalIgnoreCase))
                {
                    return BuildRawRepositoryFileUrl(
                        NormalizeRepositoryUrl(settings == null ? null : settings.ForkCatalogRepositoryUrl),
                        fork.PackageUrl.Trim());
                }

                return fork.PackageUrl.Trim();
            }

            if (string.IsNullOrWhiteSpace(fork.ReleaseTag) || string.IsNullOrWhiteSpace(fork.AssetFileName))
            {
                return string.Empty;
            }

            GitHubRepositoryCoordinates coordinates;
            if (!TryParseRepositoryUrl(NormalizeRepositoryUrl(settings == null ? null : settings.ForkCatalogRepositoryUrl), out coordinates))
            {
                return string.Empty;
            }

            return "https://github.com/"
                + coordinates.Owner
                + "/"
                + coordinates.Repository
                + "/releases/download/"
                + Uri.EscapeDataString(fork.ReleaseTag)
                + "/"
                + Uri.EscapeDataString(fork.AssetFileName);
        }

        public async Task<ForkInstallResult> DownloadAndInstallAsync(LauncherSettings settings, ForkDefinition fork, IProgress<ForkInstallProgress> progress = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (settings == null || fork == null)
            {
                return ForkInstallResult.Failed("No fork metadata is loaded.");
            }

            if (string.Equals(fork.PackageType, "repo-folder", StringComparison.OrdinalIgnoreCase))
            {
                return await DownloadAndInstallRepoFolderAsync(settings, fork, progress, cancellationToken).ConfigureAwait(false);
            }

            var packageUrl = BuildPackageUrl(settings, fork);
            if (string.IsNullOrWhiteSpace(packageUrl))
            {
                return ForkInstallResult.Failed("This version has no package source configured in Legacy-DB yet.");
            }

            Directory.CreateDirectory(settings.DownloadedForksRoot);
            ReportProgress(progress, "Installing " + fork.DisplayName, "Preparing package download...", null, true);

            var installFolderName = string.IsNullOrWhiteSpace(fork.InstallFolderName)
                ? SanitizeFolderName(fork.DisplayName ?? fork.Id)
                : fork.InstallFolderName;
            var installDirectory = Path.Combine(settings.DownloadedForksRoot, installFolderName);
            var stagingDirectory = Path.Combine(settings.DownloadedForksRoot, "_incoming", fork.Id + "-" + Guid.NewGuid().ToString("N"));
            var fileName = string.IsNullOrWhiteSpace(fork.AssetFileName)
                ? Path.GetFileName(new Uri(packageUrl).AbsolutePath)
                : fork.AssetFileName;
            var downloadPath = Path.Combine(stagingDirectory, string.IsNullOrWhiteSpace(fileName) ? "package.bin" : fileName);

            Directory.CreateDirectory(stagingDirectory);

            try
            {
                using (var response = await _httpClient.GetAsync(packageUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    response.EnsureSuccessStatusCode();

                    var totalBytes = response.Content.Headers.ContentLength;
                    var lastProgressTick = 0;
                    using (var remoteStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var localStream = File.Create(downloadPath))
                    {
                        await CopyStreamWithProgressAsync(
                            remoteStream,
                            localStream,
                            bytesCopied =>
                            {
                                var now = Environment.TickCount;
                                if (totalBytes.HasValue && bytesCopied < totalBytes.Value && unchecked(now - lastProgressTick) < 120)
                                {
                                    return;
                                }

                                lastProgressTick = now;
                                var percent = totalBytes.HasValue && totalBytes.Value > 0
                                    ? Math.Min(92d, (double)bytesCopied * 84d / totalBytes.Value)
                                    : (double?)null;
                                var message = totalBytes.HasValue && totalBytes.Value > 0
                                    ? "Downloading package... " + FormatByteSize(bytesCopied) + " of " + FormatByteSize(totalBytes.Value)
                                    : "Downloading package...";
                                ReportProgress(progress, "Installing " + fork.DisplayName, message, percent, !percent.HasValue);
                            },
                            cancellationToken).ConfigureAwait(false);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(progress, "Installing " + fork.DisplayName, "Applying package files...", 94, false);
                InstallDownloadedPackage(fork, downloadPath, installDirectory);

                fork.InstallDirectory = installDirectory;
                fork.ExecutablePath = ResolveExecutablePath(fork, installDirectory);
                fork.Enabled = File.Exists(fork.ExecutablePath);

                if (!fork.Enabled)
                {
                    return ForkInstallResult.Failed("The package downloaded, but Fire Launcher could not find the executable inside it.");
                }

                ReportProgress(progress, "Installing " + fork.DisplayName, "Install complete.", 100, false);
                return ForkInstallResult.Succeeded(installDirectory, fork.ExecutablePath);
            }
            catch (OperationCanceledException)
            {
                return ForkInstallResult.Failed("Install cancelled.");
            }
            catch (Exception ex)
            {
                return ForkInstallResult.Failed(ex.Message);
            }
            finally
            {
                TryDeleteDirectory(stagingDirectory);
            }
        }

        public string ResolveExecutablePath(ForkDefinition fork, string installDirectory)
        {
            if (fork == null || string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory))
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(fork.ExecutableRelativePath))
            {
                var explicitPath = Path.Combine(installDirectory, fork.ExecutableRelativePath);
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

        private static void InstallDownloadedPackage(ForkDefinition fork, string downloadPath, string installDirectory)
        {
            if (Directory.Exists(installDirectory))
            {
                Directory.Delete(installDirectory, true);
            }

            Directory.CreateDirectory(installDirectory);

            var packageType = string.IsNullOrWhiteSpace(fork.PackageType)
                ? Path.GetExtension(downloadPath).TrimStart('.')
                : fork.PackageType.Trim().ToLowerInvariant();

            if (packageType == "zip")
            {
                var extractRoot = Path.Combine(installDirectory, "_extract");
                ZipFile.ExtractToDirectory(downloadPath, extractRoot);

                var contentRoot = GetSingleDirectoryOrSelf(extractRoot);
                CopyDirectory(contentRoot, installDirectory, skipRootWhenSame: true);
                TryDeleteDirectory(extractRoot);
                return;
            }

            var outputName = Path.GetFileName(downloadPath);
            if (!string.IsNullOrWhiteSpace(fork.ExecutableRelativePath))
            {
                outputName = fork.ExecutableRelativePath;
            }

            var outputPath = Path.Combine(installDirectory, outputName);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? installDirectory);
            File.Copy(downloadPath, outputPath, true);
        }

        private async Task<ForkInstallResult> DownloadAndInstallRepoFolderAsync(LauncherSettings settings, ForkDefinition fork, IProgress<ForkInstallProgress> progress, CancellationToken cancellationToken)
        {
            var manifestUrl = BuildPackageUrl(settings, fork);
            if (string.IsNullOrWhiteSpace(manifestUrl))
            {
                return ForkInstallResult.Failed("This repo-backed version does not have a package manifest path yet.");
            }

            try
            {
                ReportProgress(progress, "Installing " + fork.DisplayName, "Loading package manifest...", null, true);
                cancellationToken.ThrowIfCancellationRequested();
                var manifestJson = await _httpClient.GetStringAsync(manifestUrl).ConfigureAwait(false);
                var manifest = Deserialize<ForkPackageManifest>(manifestJson);
                if (manifest == null || manifest.Files == null || manifest.Files.Count == 0)
                {
                    return ForkInstallResult.Failed("The repo package manifest is empty or invalid.");
                }

                Directory.CreateDirectory(settings.DownloadedForksRoot);
                var installFolderName = string.IsNullOrWhiteSpace(fork.InstallFolderName)
                    ? SanitizeFolderName(fork.DisplayName ?? fork.Id)
                    : fork.InstallFolderName;
                var installDirectory = Path.Combine(settings.DownloadedForksRoot, installFolderName);
                var tempDirectory = Path.Combine(settings.DownloadedForksRoot, "_incoming", fork.Id + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDirectory);

                try
                {
                    var files = manifest.Files
                        .Where(file => file != null && !string.IsNullOrWhiteSpace(file.RelativePath))
                        .ToList();
                    var totalFiles = files.Count;
                    var totalBytes = files.Sum(file => Math.Max(file.Size, 0));
                    long downloadedBytes = 0;
                    var manifestRelativePath = fork.PackageUrl.Replace("\\", "/").TrimStart('/');
                    var manifestDirectory = string.Empty;
                    var slashIndex = manifestRelativePath.LastIndexOf('/');
                    if (slashIndex >= 0)
                    {
                        manifestDirectory = manifestRelativePath.Substring(0, slashIndex);
                    }

                    var payloadRoot = string.IsNullOrWhiteSpace(manifest.PayloadRoot) ? "payload" : manifest.PayloadRoot.Trim('/').Replace("\\", "/");
                    for (var index = 0; index < files.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var file = files[index];

                        var repoRelativeFilePath = string.IsNullOrWhiteSpace(manifestDirectory)
                            ? payloadRoot + "/" + file.RelativePath.Replace("\\", "/")
                            : manifestDirectory + "/" + payloadRoot + "/" + file.RelativePath.Replace("\\", "/");
                        var fileUrl = BuildRawRepositoryFileUrl(
                            NormalizeRepositoryUrl(settings.ForkCatalogRepositoryUrl),
                            repoRelativeFilePath);
                        var localPath = Path.Combine(tempDirectory, file.RelativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(localPath) ?? tempDirectory);

                        using (var response = await _httpClient.GetAsync(fileUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            response.EnsureSuccessStatusCode();
                            var expectedSize = response.Content.Headers.ContentLength ?? Math.Max(file.Size, 0);
                            var lastProgressTick = 0;
                            using (var remoteStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (var localStream = File.Create(localPath))
                            {
                                await CopyStreamWithProgressAsync(
                                    remoteStream,
                                    localStream,
                                    currentFileBytes =>
                                    {
                                        var now = Environment.TickCount;
                                        if (expectedSize > 0 && currentFileBytes < expectedSize && unchecked(now - lastProgressTick) < 120)
                                        {
                                            return;
                                        }

                                        lastProgressTick = now;
                                        var overallBytes = downloadedBytes + currentFileBytes;
                                        double? percent = null;
                                        if (totalBytes > 0)
                                        {
                                            percent = Math.Min(94d, (double)overallBytes * 90d / totalBytes);
                                        }
                                        else if (totalFiles > 0)
                                        {
                                            percent = Math.Min(94d, ((double)index + 1d) * 90d / totalFiles);
                                        }

                                        var message = "Downloading file " + (index + 1) + " of " + totalFiles + ": " + FormatFileLabel(file.RelativePath);
                                        if (totalBytes > 0)
                                        {
                                            message += " (" + FormatByteSize(overallBytes) + " of " + FormatByteSize(totalBytes) + ")";
                                        }

                                        ReportProgress(progress, "Installing " + fork.DisplayName, message, percent, !percent.HasValue);
                                    },
                                    cancellationToken).ConfigureAwait(false);
                            }

                            downloadedBytes += expectedSize > 0 ? expectedSize : new FileInfo(localPath).Length;
                        }
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    ReportProgress(progress, "Installing " + fork.DisplayName, "Applying downloaded files...", 96, false);
                    if (Directory.Exists(installDirectory))
                    {
                        Directory.Delete(installDirectory, true);
                    }

                    CopyDirectory(tempDirectory, installDirectory, skipRootWhenSame: false);

                    fork.InstallDirectory = installDirectory;
                    fork.ExecutablePath = ResolveExecutablePath(
                        new ForkDefinition { ExecutableRelativePath = string.IsNullOrWhiteSpace(fork.ExecutableRelativePath) ? manifest.ExecutableRelativePath : fork.ExecutableRelativePath },
                        installDirectory);
                    fork.Enabled = File.Exists(fork.ExecutablePath);

                    ReportProgress(progress, "Installing " + fork.DisplayName, fork.Enabled ? "Install complete." : "Install finished, but the executable could not be found.", fork.Enabled ? 100 : 0, false);
                    return fork.Enabled
                        ? ForkInstallResult.Succeeded(installDirectory, fork.ExecutablePath)
                        : ForkInstallResult.Failed("The repo folder downloaded, but the executable still could not be found.");
                }
                finally
                {
                    TryDeleteDirectory(tempDirectory);
                }
            }
            catch (OperationCanceledException)
            {
                return ForkInstallResult.Failed("Install cancelled.");
            }
            catch (Exception ex)
            {
                return ForkInstallResult.Failed(ex.Message);
            }
        }

        private static string GetSingleDirectoryOrSelf(string extractRoot)
        {
            var directories = Directory.GetDirectories(extractRoot);
            var files = Directory.GetFiles(extractRoot);
            if (directories.Length == 1 && files.Length == 0)
            {
                return directories[0];
            }

            return extractRoot;
        }

        private static void CopyDirectory(string sourceDirectory, string destinationDirectory, bool skipRootWhenSame)
        {
            foreach (var directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var relativePath = directory.Substring(sourceDirectory.Length).TrimStart(Path.DirectorySeparatorChar);
                if (string.IsNullOrWhiteSpace(relativePath) && skipRootWhenSame)
                {
                    continue;
                }

                Directory.CreateDirectory(Path.Combine(destinationDirectory, relativePath));
            }

            foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var relativePath = file.Substring(sourceDirectory.Length).TrimStart(Path.DirectorySeparatorChar);
                var targetPath = Path.Combine(destinationDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? destinationDirectory);
                File.Copy(file, targetPath, true);
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
            }
        }

        private static void ReportProgress(IProgress<ForkInstallProgress> progress, string title, string message, double? percent, bool isIndeterminate)
        {
            if (progress == null)
            {
                return;
            }

            progress.Report(new ForkInstallProgress
            {
                Title = title,
                Message = message,
                Percent = percent,
                IsIndeterminate = isIndeterminate
            });
        }

        private static async Task CopyStreamWithProgressAsync(Stream source, Stream destination, Action<long> progressCallback, CancellationToken cancellationToken)
        {
            var buffer = new byte[81920];
            long totalBytes = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                if (bytesRead <= 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
                totalBytes += bytesRead;
                progressCallback?.Invoke(totalBytes);
            }
        }

        private static string FormatByteSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double value = bytes;
            var unitIndex = 0;

            while (value >= 1024 && unitIndex < units.Length - 1)
            {
                value /= 1024;
                unitIndex++;
            }

            return value.ToString(unitIndex == 0 ? "0" : "0.0") + " " + units[unitIndex];
        }

        private static string FormatFileLabel(string relativePath)
        {
            var fileName = Path.GetFileName(relativePath);
            return string.IsNullOrWhiteSpace(fileName) ? relativePath : fileName;
        }

        private static T Deserialize<T>(string json) where T : class
        {
            try
            {
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var serializer = new DataContractJsonSerializer(typeof(T));
                    return serializer.ReadObject(stream) as T;
                }
            }
            catch
            {
                return null;
            }
        }

        private static ForkCatalogManifest CreateFallbackCatalog()
        {
            return new ForkCatalogManifest
            {
                SchemaVersion = 1,
                UpdatedUtc = "2026-03-08T00:00:00Z",
                Forks =
                {
                    new ForkCatalogFamily
                    {
                        FamilyId = "lcemp",
                        FamilyName = "LCEMP",
                        ShowInLauncherByDefault = true,
                        Notes = "Community fork with username and server join arguments.",
                        Versions =
                        {
                            new ForkCatalogVersion
                            {
                                Id = "lcemp-v1-0-3",
                                VersionLabel = "v1.0.3",
                                DisplayName = "LCEMP v1.0.3",
                                SourceFolderName = "LCEMP v1.0.3",
                                InstallFolderName = "LCEMP v1.0.3",
                                ExecutableRelativePath = "Minecraft.Client.exe",
                                PackageType = "repo-folder",
                                PackageUrl = "forks/lcemp/v1.0.3/package.json",
                                HasMultiplayer = true,
                                SupportsUsernameArgument = true,
                                SupportsServerIpArgument = true,
                                SupportsPortArgument = false,
                                LaunchArgumentName = "-name",
                                LaunchArgumentIp = "-ip",
                                LaunchArgumentPort = string.Empty,
                                Notes = "Supports -name <username> and -ip <server>."
                            }
                        }
                    }
                }
            };
        }

        private static bool TryParseRepositoryUrl(string repositoryUrl, out GitHubRepositoryCoordinates coordinates)
        {
            coordinates = null;

            if (string.IsNullOrWhiteSpace(repositoryUrl))
            {
                return false;
            }

            Uri uri;
            if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out uri))
            {
                return false;
            }

            if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var parts = uri.AbsolutePath.Trim('/').Split('/');
            if (parts.Length < 2)
            {
                return false;
            }

            coordinates = new GitHubRepositoryCoordinates(parts[0], parts[1]);
            return true;
        }

        private string BuildRawRepositoryFileUrl(string repositoryUrl, string relativePath)
        {
            GitHubRepositoryCoordinates coordinates;
            if (!TryParseRepositoryUrl(repositoryUrl, out coordinates))
            {
                return relativePath;
            }

            return "https://raw.githubusercontent.com/"
                + coordinates.Owner
                + "/"
                + coordinates.Repository
                + "/main/"
                + relativePath.TrimStart('/').Replace("\\", "/");
        }

        private static string SanitizeFolderName(string value)
        {
            var invalidCharacters = Path.GetInvalidFileNameChars();
            var cleaned = new string((value ?? "fork").Where(character => !invalidCharacters.Contains(character)).ToArray());
            return string.IsNullOrWhiteSpace(cleaned) ? "fork" : cleaned;
        }

        private sealed class GitHubRepositoryCoordinates
        {
            public GitHubRepositoryCoordinates(string owner, string repository)
            {
                Owner = owner;
                Repository = repository;
            }

            public string Owner { get; private set; }

            public string Repository { get; private set; }
        }
    }

    internal sealed class ForkInstallResult
    {
        public static ForkInstallResult Succeeded(string installDirectory, string executablePath)
        {
            return new ForkInstallResult
            {
                Success = true,
                InstallDirectory = installDirectory,
                ExecutablePath = executablePath,
                Message = "Installed successfully."
            };
        }

        public static ForkInstallResult Failed(string message)
        {
            return new ForkInstallResult
            {
                Success = false,
                Message = string.IsNullOrWhiteSpace(message) ? "Install failed." : message
            };
        }

        public bool Success { get; private set; }

        public string Message { get; private set; }

        public string InstallDirectory { get; private set; }

        public string ExecutablePath { get; private set; }
    }

    internal sealed class ForkInstallProgress
    {
        public string Title { get; set; }

        public string Message { get; set; }

        public double? Percent { get; set; }

        public bool IsIndeterminate { get; set; }
    }
}
