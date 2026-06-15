using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Susurri.Shared.Abstractions.Diagnostics;

namespace Susurri.Shared.Infrastructure.Diagnostics;

public sealed class CrashDumpWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _directory;

    public CrashDumpWriter(string? directory = null)
    {
        _directory = string.IsNullOrWhiteSpace(directory) ? GetDefaultDirectory() : directory;
    }

    public string Directory => _directory;

    public string WriteReport(FatalErrorReport report)
    {
        System.IO.Directory.CreateDirectory(_directory);
        var ts = DateTimeOffset.Parse(report.Timestamp, CultureInfo.InvariantCulture).UtcDateTime;
        var filename = $"{ts:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.json";
        var path = Path.Combine(_directory, filename);
        var json = JsonSerializer.Serialize(report, JsonOptions);
        File.WriteAllText(path, json);
        return path;
    }

    public static string GetDefaultDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            appData = Path.Combine(home, ".config");
        }
        return Path.Combine(appData, "Susurri", "crashes");
    }
}
