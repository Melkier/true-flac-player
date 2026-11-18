namespace TrueFlac.Core;

public static class LinkAndSourceResolver
{
    public static readonly HashSet<string> RecognizedAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".wav", ".alac", ".aiff", ".ape", ".wv"
    };

    public static IReadOnlyList<string> ExpandLocalInputs(IEnumerable<string> rawInputs)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in rawInputs)
        {
            string? path = NormalizeLocalPath(raw);
            if (string.IsNullOrWhiteSpace(path)) continue;

            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                string? target = ResolveShortcutTarget(path);
                if (!string.IsNullOrWhiteSpace(target))
                    path = target;
            }

            if (Directory.Exists(path))
            {
                foreach (string f in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories))
                {
                    if (RecognizedAudioExtensions.Contains(Path.GetExtension(f)) && seen.Add(f))
                        files.Add(f);
                }
            }
            else if (File.Exists(path))
            {
                string ext = Path.GetExtension(path);
                if (ext.Equals(".m3u", StringComparison.OrdinalIgnoreCase) || ext.Equals(".m3u8", StringComparison.OrdinalIgnoreCase))
                {
                    string baseDir = Path.GetDirectoryName(path) ?? string.Empty;
                    foreach (string line in File.ReadAllLines(path))
                    {
                        string trimmed = line.Trim();
                        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#')) continue;
                        string candidate = Path.IsPathRooted(trimmed) ? trimmed : Path.GetFullPath(Path.Combine(baseDir, trimmed));
                        if (File.Exists(candidate) && seen.Add(candidate))
                            files.Add(candidate);
                    }
                }
                else if (seen.Add(path))
                {
                    files.Add(path);
                }
            }
        }

        return files;
    }

    public static string? NormalizeLocalPath(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        string s = input.Trim().Trim('"');
        if (s.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(s, UriKind.Absolute, out var uri))
            return uri.LocalPath;
        return s;
    }

    public static string? ResolveShortcutTarget(string lnkPath)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell is null) return null;
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            string? target = shortcut.TargetPath as string;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch
        {
            return null;
        }
    }
}
