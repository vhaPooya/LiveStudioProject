using Microsoft.Extensions.Configuration;

namespace LiveStudio.Infrastructure.Persistence;

/// <summary>
/// The Web and Api processes must share the same SQLite file and the same Data Protection key ring.
/// Both resolve the same data directory: Platform:DataDirectory, or "&lt;solution root&gt;/data".
/// </summary>
public static class PlatformPaths
{
    public static string ResolveDataDirectory(IConfiguration configuration)
    {
        var configured = configuration["Platform:DataDirectory"];
        string dir;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            dir = Path.GetFullPath(configured);
        }
        else
        {
            dir = Path.Combine(Directory.GetCurrentDirectory(), "data");
            var probe = new DirectoryInfo(Directory.GetCurrentDirectory());
            for (var i = 0; i < 6 && probe is not null; i++, probe = probe.Parent)
            {
                if (File.Exists(Path.Combine(probe.FullName, "LiveStudio.sln")))
                {
                    dir = Path.Combine(probe.FullName, "data");
                    break;
                }
            }
        }
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string ResolveConnectionString(IConfiguration configuration)
    {
        var explicitValue = configuration.GetConnectionString("LiveStudio") ?? configuration.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrWhiteSpace(explicitValue) && !explicitValue.Equals("Data Source=livestudio.db", StringComparison.OrdinalIgnoreCase))
            return explicitValue;
        var path = Path.Combine(ResolveDataDirectory(configuration), "livestudio.db");
        return $"Data Source={path};Default Timeout=30;Pooling=True";
    }

    public static string ResolveKeysDirectory(IConfiguration configuration)
    {
        var configured = configuration["DataProtection:KeysPath"];
        var dir = string.IsNullOrWhiteSpace(configured) || configured == ".keys"
            ? Path.Combine(ResolveDataDirectory(configuration), "keys")
            : Path.GetFullPath(configured);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
