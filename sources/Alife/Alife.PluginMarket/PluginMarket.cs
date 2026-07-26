using Alife.Platform;
using Newtonsoft.Json;

namespace Alife.PluginMarket;

public interface IPluginProvider
{
    /// <summary>
    /// 获取托管的所有插件信息
    /// </summary>
    /// <returns></returns>
    public Task<Plugin[]> GetPluginsAsync();
}

public interface IPluginResolver
{
    /// <summary>
    /// 以某种途径解析出当前存在的插件id和version
    /// </summary>
    /// <returns></returns>
    public Dictionary<string, string> GetPlugins();
}

public interface IEnvironmentInstaller
{
    /// <summary>
    /// 安装依赖环境清单
    /// key：包名。可能重复。
    /// value：版本要求.可能冲突。允许如下几种形式：
    ///     >=x.x.x : 最小版本
    ///     &lt;=x.x.x : 最大版本
    ///     ==x.x.x : 精确版本（同时限制上下限）
    ///     空字符串 : 等于 >=0.0.0（无限制）
    /// </summary>
    /// <param name="environment"></param>
    public void InstallEnvironment(IEnumerable<KeyValuePair<string, string>> environment);
}

public interface IPluginInstaller
{
    /// <summary>
    /// 不需要考虑环境依赖，仅重新安装插件本体
    /// </summary>
    /// <param name="plugin"></param>
    /// <param name="version"></param>
    public Task InstallPlugin(Plugin plugin, string version);
    public Task UninstallPlugin(Plugin plugin);
}

public class PluginMarket
{
    /// <summary>
    /// 从云端拉取插件列表，并写入缓存
    /// </summary>
    public async Task FetchOnlinePluginsAsync()
    {
        allPlugins = (await onlinePlugins.GetPluginsAsync()).ToDictionary(plugin => plugin.Id, plugin => plugin);
        SaveCache();
    }
    /// <summary>
    /// 抓取本地已安装插件列表
    /// </summary>
    public void FetchLocalPlugins()
    {
        hadPlugins = localPlugins.GetPlugins();
    }


    public IEnumerable<Plugin> GetAllPlugins()
    {
        return allPlugins.Values;
    }
    public Dictionary<string, string> GetInstalledPlugins()
    {
        return hadPlugins;
    }

    public List<string> GetDependents(string pluginId)
    {
        List<string> dependents = new();
        foreach ((string id, string version) in hadPlugins)
        {
            if (id == pluginId)
                continue;

            Plugin? plugin = allPlugins.GetValueOrDefault(id);
            if (plugin == null)
                continue;

            Dictionary<string, string>? dependencies = plugin.GetDependencies(version);
            if (dependencies != null && dependencies.ContainsKey(pluginId))
                dependents.Add(id);
        }
        return dependents;
    }

    public async Task InstallPlugin(Plugin plugin, string version)
    {
        if (plugin.Releases == null)
            throw new Exception($"Plugin {plugin.Id} is not released");
        if (plugin.Releases.ContainsKey(version) == false)
            throw new Exception($"Plugin {plugin.Id} version {version} not released");

        var dependencies = plugin.GetDependencies(version);
        if (dependencies != null)
        {
            VersionResolver versionResolver = new();
            versionResolver.AddRange(dependencies);

            foreach (KeyValuePair<string, string> pair in dependencies)
            {
                string? hadVersion = hadPlugins.GetValueOrDefault(pair.Key);
                if (hadVersion != null && versionResolver.IsSatisfied(pair.Key, hadVersion))
                    continue;

                Plugin? dependentPlugin = allPlugins.GetValueOrDefault(pair.Key);
                if (dependentPlugin == null)
                    throw new Exception($"Plugin {plugin.Id} dependent unknown plugin type");
                IEnumerable<string>? versionList = dependentPlugin.Releases?.Keys;
                if (versionList == null)
                    throw new Exception($"Plugin {plugin.Id} dependent plugin is not released");

                string satisfiedVersion = versionResolver.Resolve(dependentPlugin.Id, versionList);
                await InstallPlugin(dependentPlugin, satisfiedVersion);
            }
        }

        var environments = plugin.GetEnvironments(version);
        if (environments != null)
        {
            List<KeyValuePair<string, string>> environmentManifest = new();
            foreach ((string type, Dictionary<string, string> environment) in environments)
            {
                IEnvironmentInstaller? environmentInstaller = environmentInstallers.GetValueOrDefault(type);
                if (environmentInstaller == null)
                    throw new Exception($"Plugin {plugin.Id} has no supported environment type");

                GetEnvironment(type, environmentManifest);
                environmentManifest.AddRange(environment);
                environmentInstaller.InstallEnvironment(environmentManifest);
            }
        }

        await pluginInstaller.InstallPlugin(plugin, version);
    }
    public async Task InstallPlugins(IEnumerable<(Plugin plugin, string version)> plugins)
    {
        var pluginList = plugins.ToList();

        // 收集完全要装的全部插件
        Dictionary<string, (Plugin plugin, string version)> installPlan = new();
        VersionResolver versionResolver = new();
        foreach (var (plugin, version) in pluginList)
            CollectDependencies(plugin, version);

        void CollectDependencies(Plugin plugin, string version)
        {
            if (installPlan.ContainsKey(plugin.Id)) return;

            var dependencies = plugin.GetDependencies(version);
            if (dependencies != null)
            {
                versionResolver.AddRange(dependencies);
                foreach (var (depId, _) in dependencies)
                {
                    string? hadVersion = hadPlugins.GetValueOrDefault(depId);
                    if (hadVersion != null && versionResolver.IsSatisfied(depId, hadVersion))
                        continue;//插件已按照，跳过

                    Plugin? depPlugin = allPlugins.GetValueOrDefault(depId);
                    if (depPlugin == null)
                        throw new Exception($"Unknown plugin: {depId}");
                    IEnumerable<string>? versionList = depPlugin.Releases?.Keys;
                    if (versionList == null)
                        throw new Exception($"Plugin {depId} not released");

                    string resolved = versionResolver.Resolve(depId, versionList);
                    CollectDependencies(depPlugin, resolved);
                }
            }

            installPlan[plugin.Id] = (plugin, version);
        }

        // 校验所有插件有合法
        foreach (var (id, entry) in installPlan)
        {
            if (entry.plugin.Releases == null || !entry.plugin.Releases.ContainsKey(entry.version))
                throw new Exception($"Plugin {id} version {entry.version} not released");
        }

        // 统一安装环境依赖
        HashSet<string> envTypes = new();
        foreach (var (_, entry) in installPlan)
        {
            var envs = entry.plugin.GetEnvironments(entry.version);
            if (envs != null)
                foreach (var type in envs.Keys)
                    envTypes.Add(type);
        }

        foreach (string type in envTypes)
        {
            if (!environmentInstallers.TryGetValue(type, out var installer))
                throw new Exception($"No installer for environment type: {type}");

            List<KeyValuePair<string, string>> manifest = new();
            GetEnvironment(type, manifest);

            foreach (var (_, entry) in installPlan)
            {
                var envs = entry.plugin.GetEnvironments(entry.version)?.GetValueOrDefault(type);
                if (envs != null)
                    manifest.AddRange(envs);
            }

            installer.InstallEnvironment(manifest);
        }

        // 逐个安装插件本体
        foreach (var (_, entry) in installPlan)
        {
            await pluginInstaller.InstallPlugin(entry.plugin, entry.version);
        }
    }
    public async Task UninstallPlugin(Plugin plugin)
    {
        await pluginInstaller.UninstallPlugin(plugin);
    }



    readonly IPluginProvider onlinePlugins;
    readonly IPluginResolver localPlugins;
    readonly IPluginInstaller pluginInstaller;
    readonly Dictionary<string, IEnvironmentInstaller> environmentInstallers;
    Dictionary<string, Plugin> allPlugins;
    Dictionary<string, string> hadPlugins;
    readonly string cacheDir = Path.Combine(AlifePath.RuntimeFolderPath, "PluginMarket");

    public PluginMarket(IPluginProvider onlinePlugins, IPluginResolver localPlugins, IPluginInstaller pluginInstaller, Dictionary<string, IEnvironmentInstaller> environmentInstallers)
    {
        this.onlinePlugins = onlinePlugins;
        this.localPlugins = localPlugins;
        this.pluginInstaller = pluginInstaller;
        this.environmentInstallers = environmentInstallers;

        allPlugins = new Dictionary<string, Plugin>();
        hadPlugins = [];

        LoadCache();
        FetchLocalPlugins();
    }

    void GetEnvironment(string type, List<KeyValuePair<string, string>> environments)
    {
        environments.Clear();
        foreach ((string pluginID, string version) in hadPlugins)
        {
            Plugin? plugin = allPlugins.GetValueOrDefault(pluginID);
            if (plugin == null)
                continue;
            Dictionary<string, string>? environment = plugin.GetEnvironments(version)?.GetValueOrDefault(type);
            if (environment == null)
                continue;

            environments.AddRange(environment);
        }
    }

    void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(cacheDir);
            foreach (var plugin in Directory.GetFiles(cacheDir))
                File.Delete(plugin);
            foreach ((string _, Plugin plugin) in allPlugins)
                File.WriteAllText(Path.Combine(cacheDir, $"{plugin.Id}.json"), JsonConvert.SerializeObject(plugin, Formatting.Indented));
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }
    void LoadCache()
    {
        try
        {
            if (!Directory.Exists(cacheDir))
                return;

            foreach (string pluginFile in Directory.GetFiles(cacheDir, "*.json"))
            {
                try
                {
                    string json = File.ReadAllText(pluginFile);
                    Plugin? plugin = JsonConvert.DeserializeObject<Plugin>(json);
                    if (plugin != null)
                        allPlugins[plugin.Id] = plugin;
                }
                catch (Exception ex)
                {
                    File.Delete(pluginFile);
                    Console.WriteLine(ex);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }
}
