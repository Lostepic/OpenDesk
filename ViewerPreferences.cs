using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenDesk;

internal sealed class ViewerPreferences
{
    private readonly string file = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenDesk", "viewers.json");
    private readonly Settings settings;

    internal ViewerPreferences()
    {
        Settings? saved = null;
        if (File.Exists(file))
        {
            try { saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(file)); }
            catch (JsonException) { }
        }
        settings = saved ?? new Settings();
        if (saved is null)
            Save();
    }

    internal string HostId => settings.HostId;
    internal Viewer? Find(string key) => settings.Viewers.GetValueOrDefault(Hash(key));

    internal Viewer Approve(string key)
    {
        string hash = Hash(key);
        var viewer = settings.Viewers.GetValueOrDefault(hash) ?? new Viewer();
        settings.Viewers[hash] = viewer;
        viewer.Paused = false;
        Save();
        return viewer;
    }

    internal void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings));
        File.Move(temp, file, overwrite: true);
    }

    private static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(key)));

    private sealed class Settings
    {
        public string HostId { get; set; } = Guid.NewGuid().ToString("N");
        public Dictionary<string, Viewer> Viewers { get; set; } = new(StringComparer.Ordinal);
    }

    internal sealed class Viewer
    {
        public uint? MonitorId { get; set; }
        public Position? Layout { get; set; }
        public bool Paused { get; set; }
    }

    internal sealed record Position(int X, int Y)
    {
        internal Point ToPoint() => new(X, Y);
    }
}
