using System.Collections.Generic;
using System.Runtime.Serialization;

namespace FireLauncher.Models
{
    [DataContract]
    public sealed class ForkCatalogManifest
    {
        [DataMember]
        public int SchemaVersion { get; set; }

        [DataMember]
        public string UpdatedUtc { get; set; }

        [DataMember]
        public List<ForkCatalogFamily> Forks { get; set; } = new List<ForkCatalogFamily>();
    }

    [DataContract]
    public sealed class ForkCatalogFamily
    {
        [DataMember]
        public string FamilyId { get; set; }

        [DataMember]
        public string FamilyName { get; set; }

        [DataMember]
        public bool ShowInLauncherByDefault { get; set; }

        [DataMember]
        public string Notes { get; set; }

        [DataMember]
        public List<ForkCatalogVersion> Versions { get; set; } = new List<ForkCatalogVersion>();
    }

    [DataContract]
    public sealed class ForkCatalogVersion
    {
        [DataMember]
        public string Id { get; set; }

        [DataMember]
        public string VersionLabel { get; set; }

        [DataMember]
        public string DisplayName { get; set; }

        [DataMember]
        public string SourceFolderName { get; set; }

        [DataMember]
        public string InstallFolderName { get; set; }

        [DataMember]
        public string ExecutableRelativePath { get; set; }

        [DataMember]
        public string PackageType { get; set; }

        [DataMember]
        public string PackageUrl { get; set; }

        [DataMember]
        public string ReleaseTag { get; set; }

        [DataMember]
        public string AssetFileName { get; set; }

        [DataMember]
        public bool HasMultiplayer { get; set; }

        [DataMember]
        public bool SupportsUsernameArgument { get; set; }

        [DataMember]
        public bool SupportsServerIpArgument { get; set; }

        [DataMember]
        public bool SupportsPortArgument { get; set; }

        [DataMember]
        public string LaunchArgumentName { get; set; }

        [DataMember]
        public string LaunchArgumentIp { get; set; }

        [DataMember]
        public string LaunchArgumentPort { get; set; }

        [DataMember]
        public string Notes { get; set; }
    }
}
