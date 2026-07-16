using System;
using System.IO;
using System.Linq;

namespace Susurri.GUI.Services;

public static class GuiSettings
{
    public static string[] LoadBootstrapNodes()
    {
        try
        {
            var path = BootstrapNodesPath();
            if (!File.Exists(path))
                return Array.Empty<string>();
            return File.ReadAllLines(path)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Distinct()
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static void SaveBootstrapNodes(string[] nodes)
    {
        try
        {
            var path = BootstrapNodesPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, nodes);
        }
        catch
        {
        }
    }

    private static string BootstrapNodesPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Susurri", "bootstrap-nodes");
}
