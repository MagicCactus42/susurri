using Susurri.Shared.Abstractions.Security;

namespace Susurri.CLI;

internal static class Downloads
{
    public static string Directory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(home, "Downloads");
        var target = System.IO.Directory.Exists(downloads)
            ? Path.Combine(downloads, "susurri")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Susurri", "downloads");

        System.IO.Directory.CreateDirectory(target);
        LocalEncryption.RestrictDirectory(target);
        return target;
    }

    public static string Save(string fileName, byte[] data)
    {
        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = "received.bin";

        var path = UniquePath(Path.Combine(Directory(), safeName));
        File.WriteAllBytes(path, data);
        RestrictFile(path);
        return path;
    }

    private static void RestrictFile(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
        }
    }

    private static string UniquePath(string desired)
    {
        if (!File.Exists(desired))
            return desired;

        var dir = Path.GetDirectoryName(desired)!;
        var stem = Path.GetFileNameWithoutExtension(desired);
        var ext = Path.GetExtension(desired);

        for (var i = 1; i < 10_000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(dir, $"{stem}-{Guid.NewGuid():N}{ext}");
    }
}
