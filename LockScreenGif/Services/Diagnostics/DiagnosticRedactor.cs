using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LockscreenGif.Models.Diagnostics;

namespace LockscreenGif.Services.Diagnostics;

/// <summary>Report-scoped aliases remove personal identifiers without modifying the live sessions.</summary>
public sealed class DiagnosticRedactor
{
    private readonly Dictionary<string, string> aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> sourcePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> sourceNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> cacheDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> policyImages = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> originalImages = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex Sid = new(@"\bS-1-\d+(?:-\d+){1,14}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LocalPath = new(
        "(?<![\\w])(?:[A-Za-z]:[\\\\/]|\\\\\\\\)[^\\r\\n\"<>|;\\]\\)]*",
        RegexOptions.CultureInvariant
    );
    private static readonly Regex Url = new(
        @"\b(?:https?|ftp|file)://[^\s<>""\\]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );
    private static readonly Regex CacheFile = new(
        @"^LockScreen(?:_{1,3}\d{1,5}_\d{1,5}(?:_notdimmed)?)?\.jpg$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );
    private static readonly Regex CacheFolder = new(@"^LockScreen_[A-Z]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public DiagnosticRedactor(DiagnosticSession session, DiagnosticSession? comparison = null)
    {
        Register(session);
        if (comparison is not null)
        {
            Register(comparison);
        }
    }

    private void Register(DiagnosticSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.SourcePath))
        {
            sourcePaths.Add(session.SourcePath);
        }

        if (!string.IsNullOrWhiteSpace(session.SourceName))
        {
            sourceNames.Add(session.SourceName);
        }

        RegisterEnvironment(session.Environment);
        foreach (var observation in session.EnvironmentObservations)
        {
            RegisterEnvironment(observation.Values);
        }
    }

    private void RegisterEnvironment(Dictionary<string, string> environment)
    {
        if (environment.TryGetValue("Cache directory", out var root) && !string.IsNullOrWhiteSpace(root))
        {
            cacheDirectories.Add(root.TrimEnd('\\', '/'));
        }

        foreach (var pair in environment)
        {
            if (string.IsNullOrWhiteSpace(pair.Value) || IsUnavailable(pair.Value))
            {
                continue;
            }

            if (pair.Key.EndsWith("/ LockScreenImage", StringComparison.OrdinalIgnoreCase) && pair.Value.Length > 3)
            {
                policyImages.Add(pair.Value);
            }

            if (pair.Key.StartsWith("Original lock-screen image", StringComparison.OrdinalIgnoreCase))
            {
                originalImages.Add(pair.Value);
            }
        }
    }

    public string Redact(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        var value = input;
        foreach (var path in sourcePaths.OrderByDescending(path => path.Length))
        {
            value = value.Replace(path, "<source GIF>", StringComparison.OrdinalIgnoreCase);
        }
        // A forced-image policy can contain a bare filename, a URL with credentials, or a personal path.
        foreach (var policy in policyImages.OrderByDescending(policy => policy.Length))
        {
            value = value.Replace(policy, Alias("policy-image", policy), StringComparison.OrdinalIgnoreCase);
        }

        foreach (var original in originalImages.OrderByDescending(original => original.Length))
        {
            value = value.Replace(original, Alias("original-image", original), StringComparison.OrdinalIgnoreCase);
        }

        value = Url.Replace(value, match => Alias("url", match.Value));
        value = LocalPath.Replace(value, match => AliasPath(match.Value));
        value = Sid.Replace(value, match => Alias("sid", match.Value));
        foreach (var name in sourceNames.OrderByDescending(name => name.Length))
        {
            value = value.Replace(name, "<selected GIF>", StringComparison.OrdinalIgnoreCase);
        }

        value = ReplaceIdentity(value, Environment.UserName, "<user>");
        value = ReplaceIdentity(value, Environment.MachineName, "<machine>");
        return value;
    }

    public JsonNode? Redact(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(pair => pair.Key).ToArray())
            {
                obj[key] = Redact(obj[key]?.DeepClone());
            }

            return obj;
        }
        if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                array[i] = Redact(array[i]?.DeepClone());
            }

            return array;
        }
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? JsonValue.Create(Redact(text)) : node;
    }

    private string AliasPath(string path)
    {
        var trimmed = path.TrimEnd(' ', '.', ',');
        var normalized = trimmed.Replace('/', '\\');
        var root = cacheDirectories.FirstOrDefault(candidate =>
            string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(candidate + "\\", StringComparison.OrdinalIgnoreCase)
        );
        if (root is not null)
        {
            var relative = normalized[root.Length..].TrimStart('\\');
            if (relative.Length == 0)
            {
                return "<cache>" + path[trimmed.Length..];
            }
            // Only Windows-generated cache names are public; arbitrary names may contain personal data.
            var parts = relative.Split('\\');
            var publicParts = parts.Select(
                (part, index) =>
                    index == parts.Length - 1 && CacheFile.IsMatch(part) || index < parts.Length - 1 && CacheFolder.IsMatch(part)
                        ? part
                        : Alias("cache-item", root + "\\" + string.Join("\\", parts.Take(index + 1)))
            );
            return "<cache>/" + string.Join("/", publicParts) + path[trimmed.Length..];
        }
        var fileName = normalized.Split('\\').Last();
        var suffix = CacheFile.IsMatch(fileName) ? "/" + fileName : "";
        return Alias("path", trimmed) + suffix + path[trimmed.Length..];
    }

    private string Alias(string category, string value)
    {
        var key = category + ":" + value;
        if (!aliases.TryGetValue(key, out var alias))
        {
            aliases[key] = alias = $"<{category}-{aliases.Count + 1}>";
        }

        return alias;
    }

    private static bool IsUnavailable(string value) =>
        value.Equals("Not present", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Not available", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("Unavailable (", StringComparison.OrdinalIgnoreCase);

    private static string ReplaceIdentity(string text, string identity, string replacement) =>
        string.IsNullOrEmpty(identity)
            ? text
            : Regex.Replace(text, @"(?<![\w])" + Regex.Escape(identity) + @"(?![\w])", replacement, RegexOptions.IgnoreCase);
}
