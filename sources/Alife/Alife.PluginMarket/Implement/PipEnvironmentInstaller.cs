using Alife.Platform;

namespace Alife.PluginMarket;

public class PipEnvironmentInstaller(string packageListOutput) : IEnvironmentInstaller
{
    static bool setuptoolsReady;

    public void InstallEnvironment(IEnumerable<KeyValuePair<string, string>> environment)
    {
        if (!setuptoolsReady)
        {
            AlifePlatform.Command("python", "-m pip install setuptools wheel --quiet");
            setuptoolsReady = true;
        }

        File.WriteAllLines(
            packageListOutput,
            environment.Select(dep => $"{dep.Key}{dep.Value}")
        );
        AlifePlatform.Command("python", $"-m pip install -r \"{packageListOutput}\"");
    }
}
