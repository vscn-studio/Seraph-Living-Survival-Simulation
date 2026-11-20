using System.Text.RegularExpressions;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Seraph;

/// <summary>Player-language text with English/readable fallbacks and bounded missing-key diagnostics.</summary>
internal static class LocalizedText
{
    private static readonly HashSet<string> reported = new(StringComparer.Ordinal);
    internal static ILogger? Logger { get; set; }
    internal static string Get(string code, params object[] args) => GetL(null, code, args);
    internal static string GetL(string? language, string code, params object[] args)
        => TryGetL(language, code, out var text, args) ? text
            : Regex.Replace(code[(code.IndexOf(':') + 1)..].Replace('-', ' '), "([a-z])([A-Z])", "$1 $2");

    internal static bool TryGet(string code, out string text, params object[] args)
        => TryGetL(null, code, out text, args);

    private static bool TryGetL(string? language, string code, out string text, params object[] args)
    {
        string? Read(string? locale)
        {
            try
            {
                // These are exact mod keys. Avoid Lang.GetUnformatted's wildcard search
                // and native missing-key logging; this class owns the bounded diagnostic.
                if (!Lang.AvailableLanguages.TryGetValue(locale ?? Lang.CurrentLocale, out var service)) return null;
                var template = service.GetUnformatted(code);
                if (template == code) return null;
                // Validate before native formatting: its error logger may be unavailable during startup.
                _ = string.Format(template, args);
                var value = service.Get(code, args);
                return value == code || Regex.IsMatch(value, @"(?<!\{)\{\d+(?:[,}:])") ? null : value;
            }
            catch (FormatException) { return null; }
        }
        if (Read(language) is { } found) { text = found; return true; }
        lock (reported)
            if (reported.Count < 1024 && reported.Add((language ?? "client") + ":" + code))
                Logger?.Warning("[vsslssmod] Missing or invalid translation: {0} ({1})", code, language ?? "client");
        text = Read("en") ?? "";
        return text.Length > 0;
    }
}
