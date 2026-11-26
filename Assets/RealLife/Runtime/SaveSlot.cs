using System;
using System.IO;
using System.Threading.Tasks;
using RealLife.Simulation;
using UnityEngine;

namespace RealLife.Runtime
{
    internal sealed class SaveSlot
    {
        private readonly string directory;
        private readonly string name;
        public string CurrentPath { get; }
        public string PreviousPath { get; }

        public SaveSlot(string directory, string name = "slot-01")
        {
            this.directory = directory;
            this.name = name;
            CurrentPath = Path.Combine(directory, name + ".json");
            PreviousPath = Path.Combine(directory, name + ".previous.json");
        }

        public bool Exists => File.Exists(CurrentPath) || File.Exists(PreviousPath);

        public Task WriteAsync<T>(T snapshot, bool preservePrevious)
        {
            var json = JsonUtility.ToJson(snapshot, true);
            return Task.Run(() => Write(json, preservePrevious));
        }

        public static SessionSnapshot Read(string path) => Read<SessionSnapshot>(path);

        public static T Read<T>(string path) => JsonUtility.FromJson<T>(File.ReadAllText(path));

        private void Write(string json, bool preservePrevious)
        {
            Directory.CreateDirectory(directory);
            var pending = Path.Combine(directory, $"{name}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var writer = new StreamWriter(file, new System.Text.UTF8Encoding(false), 4096, true))
                        writer.Write(json);
                    file.Flush(flushToDisk: true);
                }
                if (File.Exists(CurrentPath)) File.Replace(pending, CurrentPath, preservePrevious ? null : PreviousPath);
                else File.Move(pending, CurrentPath);
            }
            finally
            {
                if (File.Exists(pending)) File.Delete(pending);
            }
        }
    }
}
