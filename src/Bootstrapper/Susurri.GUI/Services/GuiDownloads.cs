using System;
using System.IO;

namespace Susurri.GUI.Services;

public static class GuiDownloads
{
    public static string Save(string fileName, byte[] data)
    {
        var safe = Sanitize(fileName);
        var directory = ResolveDirectory();
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, safe);
        var stem = Path.GetFileNameWithoutExtension(safe);
        var extension = Path.GetExtension(safe);
        var counter = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(directory, $"{stem} ({counter}){extension}");
            counter++;
        }

        File.WriteAllBytes(path, data);
        return path;
    }

    private static string ResolveDirectory()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            var downloads = Path.Combine(profile, "Downloads");
            if (Directory.Exists(downloads))
                return Path.Combine(downloads, "susurri");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Susurri", "downloads");
    }

    private static string Sanitize(string fileName)
    {
        var name = Path.GetFileName(fileName.Trim());
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "file" : name;
    }
}
