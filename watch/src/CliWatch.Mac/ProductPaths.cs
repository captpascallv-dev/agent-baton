// SPDX-License-Identifier: MPL-2.0
namespace CliWatch.App;
public static class ProductPaths
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "AgentBatonWatch");
    public static string ConfigFile => Path.Combine(DataDirectory, "config.json");
}
