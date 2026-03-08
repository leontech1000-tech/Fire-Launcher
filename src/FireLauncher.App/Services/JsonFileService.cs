using System;
using System.IO;
using System.Runtime.Serialization.Json;

namespace FireLauncher.Services
{
    internal static class JsonFileService
    {
        public static T Load<T>(string path) where T : class
        {
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                using (var stream = File.OpenRead(path))
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

        public static void Save<T>(string path, T instance) where T : class
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

            using (var stream = File.Create(path))
            {
                var serializer = new DataContractJsonSerializer(typeof(T));
                serializer.WriteObject(stream, instance);
            }
        }
    }
}
