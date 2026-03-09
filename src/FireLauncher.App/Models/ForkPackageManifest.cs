using System.Collections.Generic;
using System.Runtime.Serialization;

namespace FireLauncher.Models
{
    [DataContract]
    public sealed class ForkPackageManifest
    {
        [DataMember]
        public string PackageId { get; set; }

        [DataMember]
        public string PayloadRoot { get; set; }

        [DataMember]
        public string ExecutableRelativePath { get; set; }

        [DataMember]
        public List<ForkPackageFile> Files { get; set; } = new List<ForkPackageFile>();
    }

    [DataContract]
    public sealed class ForkPackageFile
    {
        [DataMember]
        public string RelativePath { get; set; }

        [DataMember]
        public long Size { get; set; }
    }
}
