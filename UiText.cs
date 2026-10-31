using System.Text.Json;

namespace VoiceAec;

// Single source of truth for the interface language. The backend owns the
// state: the panel injects it at render time, every API response and every
// native UI label reads it through S/F, and POST /api/lang persists it.
internal static class UiText
{
    public const string Chinese = "zh";
    public const string English = "en";

    private static int english;

    /// <summary>Current language code, either "zh" or "en".</summary>
    public static string Lang => Volatile.Read(ref english) == 0 ? Chinese : English;

    public static bool IsEnglish => Volatile.Read(ref english) != 0;

    /// <summary>Raised after a successful language change (any thread).</summary>
    public static event Action? LanguageChanged;

    /// <summary>Normalizes a stored value: "en…" (trimmed, case-insensitive) is English, everything else Chinese.</summary>
    public static string NormalizeLang(string? value) =>
        TryNormalizeLang(value, out var lang) ? lang : Chinese;

    /// <summary>Accepts only zh/en values (trimmed, case-insensitive, optional region suffix).</summary>
    public static bool TryNormalizeLang(string? value, out string lang)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.StartsWith(English, StringComparison.OrdinalIgnoreCase))
        {
            lang = English;
            return true;
        }
        if (trimmed.StartsWith(Chinese, StringComparison.OrdinalIgnoreCase))
        {
            lang = Chinese;
            return true;
        }
        lang = Chinese;
        return false;
    }

    /// <summary>Applies a language value; unknown values fall back to Chinese. Returns true when the language changed.</summary>
    public static bool SetLang(string? value)
    {
        var next = NormalizeLang(value) == English ? 1 : 0;
        var changed = Interlocked.Exchange(ref english, next) != next;
        if (changed) LanguageChanged?.Invoke();
        return changed;
    }

    // ── Persisted language for the standalone elevated helper ────────────
    // The OBS helper runs in its own process (spawned elevated) before the
    // normal config initialization in Program.cs, so it applies the saved
    // language itself, using the same voiceaec.json lookup order as the app.

    public static readonly string PortableConfigPath =
        Path.Combine(AppContext.BaseDirectory, "voiceaec.json");

    public static readonly string FallbackConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VoiceAec", "voiceaec.json");

    /// <summary>Config file the app uses: the LocalAppData copy wins when present, otherwise the portable one.</summary>
    public static string ResolveConfigPath() => ResolveConfigPath(FallbackConfigPath, PortableConfigPath);

    /// <summary>Same choice, with explicit paths so tests can cover every install shape.</summary>
    public static string ResolveConfigPath(string fallbackPath, string portablePath)
    {
        if (File.Exists(fallbackPath)) return fallbackPath;
        if (File.Exists(portablePath)) return portablePath;
        return fallbackPath;
    }

    /// <summary>Reads "lang" from the persisted config and applies it. Missing or unreadable values mean Chinese.</summary>
    public static bool ApplySavedLanguage() => ApplySavedLanguage(ResolveConfigPath());

    public static bool ApplySavedLanguage(string configPath)
    {
        string? saved = null;
        try
        {
            if (File.Exists(configPath))
            {
                var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(configPath));
                if (settings != null && settings.TryGetValue("lang", out var value)) saved = value;
            }
        }
        catch { }   // an unreadable config keeps the default language
        return SetLang(saved);
    }

    /// <summary>Picks the text for the current language.</summary>
    public static string S(string zh, string en) => IsEnglish ? en : zh;

    /// <summary>Picks the format string for the current language, then formats it with invariant culture.</summary>
    public static string F(string zhFormat, string enFormat, params object?[] args) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, IsEnglish ? enFormat : zhFormat, args);
}
