using System;

namespace Alife.Function.AIModelUtility;

/// <summary>
/// Compatibility facade for plugins that predate the ModelDownloader rename.
/// </summary>
[Obsolete("Use ModelDownloader instead.")]
public static class AIModelUtility
{
    public static string ModelScopeModelPath => ModelDownloader.ModelScopeModelPath;

    public static string EnsureModelExisting(string modelId, string? targetFile = null) =>
        ModelDownloader.EnsureModelExisting(modelId, targetFile);
}
