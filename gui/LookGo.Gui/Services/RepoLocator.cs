namespace LookGo.Gui.Services;

public static class RepoLocator
{
    public static string? FindRepositoryRoot(string[]? args = null)
    {
        if (args is not null)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--repo", StringComparison.OrdinalIgnoreCase))
                {
                    var candidate = args[i + 1];
                    if (LooksLikeRepo(candidate))
                        return Path.GetFullPath(candidate);
                }
            }
        }

        var env = Environment.GetEnvironmentVariable("LOOKGO_REPO");
        if (!string.IsNullOrWhiteSpace(env) && LooksLikeRepo(env))
            return Path.GetFullPath(env);

        return WalkUp(Environment.CurrentDirectory) ?? WalkUp(AppContext.BaseDirectory);
    }

    private static string? WalkUp(string startPath)
    {
        try
        {
            var dir = new DirectoryInfo(startPath);
            while (dir is not null)
            {
                if (LooksLikeRepo(dir.FullName))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        catch
        {
        }
        return null;
    }

    private static bool LooksLikeRepo(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
            return false;
        try
        {
            return File.Exists(Path.Combine(dir, "lookgo.py")) &&
                   Directory.Exists(Path.Combine(dir, "lookgo"));
        }
        catch
        {
            return false;
        }
    }
}
