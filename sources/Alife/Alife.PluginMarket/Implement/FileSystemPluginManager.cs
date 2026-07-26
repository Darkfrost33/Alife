using Alife.Platform;

namespace Alife.PluginMarket;

public class FileSystemPluginManager : IPluginResolver, IPluginInstaller
{
    public FileSystemPluginManager(string rootPath)
    {
        Directory.CreateDirectory(rootPath);
        this.rootPath = rootPath;
    }
    public Dictionary<string, string> GetPlugins()
    {
        Dictionary<string, string> dictionary = new();
        foreach (string directory in Directory.GetDirectories(rootPath))
        {
            string versionPath = Path.Combine(directory, VersionFile);
            if (File.Exists(versionPath) == false)
                continue;

            string version = File.ReadAllText(versionPath);
            string pluginID = Path.GetFileName(directory);
            dictionary.Add(pluginID, version);
        }
        return dictionary;
    }
    public async Task InstallPlugin(Plugin plugin, string version)
    {
        if (plugin.Releases == null)
            throw new Exception($"Plugin {plugin.Id} is not released");

        PluginRelease? pluginRelease = plugin.Releases.GetValueOrDefault(version);
        if (pluginRelease == null)
            throw new Exception($"Plugin {plugin.Id} version {version} not released");

        string pluginDirectory = Path.Combine(rootPath, plugin.Id);

        if (!string.IsNullOrWhiteSpace(pluginRelease.File))
        {
            string extension = Path.GetExtension(pluginRelease.File);
            switch (extension)
            {
                case ".zip":
                    await UninstallPlugin(plugin);
                    await AlifePlatform.DownloadZipFileAsync(pluginDirectory, pluginRelease.File);
                    break;
                default:
                    throw new Exception($"Plugin {plugin.Id} file type is not supported");
            }
        }
        else
        {
            Directory.CreateDirectory(pluginDirectory);
        }

        string versionPath = Path.Combine(pluginDirectory, VersionFile);
        await File.WriteAllTextAsync(versionPath, version);
    }
    public Task UninstallPlugin(Plugin plugin)
    {
        string pluginPath = Path.Combine(rootPath, plugin.Id);
        if (Directory.Exists(pluginPath))
            Directory.Delete(pluginPath, true);
        return Task.CompletedTask;
    }

    const string VersionFile = "VERSION.txt";
    readonly string rootPath;
}
