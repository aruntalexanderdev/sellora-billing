using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Sellora.DbMigrator;

public enum ScriptKind { EfMigration, Data, Repeatable, Validation }

public enum ViolationMode { Warn, Block }

/// <summary>One SQL file from the release package, with the options from its <c>-- sellora:</c> header.</summary>
public sealed record SqlScript(
    string Name,
    ScriptKind Kind,
    string Sql,
    string Checksum,
    string? Description,
    TimeSpan Timeout,
    TimeSpan LockTimeout,
    bool UseTransaction,
    ViolationMode? OnViolation);

/// <summary>
/// Loads the release scripts from <c>&lt;root&gt;/scripts</c> (one-time data), <c>&lt;root&gt;/repeatable</c> (<c>R__</c>) and
/// <c>&lt;root&gt;/validation</c> (<c>V__</c>). Every problem is collected so one run reports all of them.
/// </summary>
public static partial class ScriptCatalog
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(30);

    [GeneratedRegex(@"^\d{4}\.\d{2}\.\d{2}\.\d{3}__[a-z0-9]+(-[a-z0-9]+)*\.sql$")]
    private static partial Regex DataName();

    [GeneratedRegex(@"^R__[a-z0-9]+(-[a-z0-9]+)*\.sql$")]
    private static partial Regex RepeatableName();

    [GeneratedRegex(@"^V__[a-z0-9]+(-[a-z0-9]+)*\.sql$")]
    private static partial Regex ValidationName();

    [GeneratedRegex(@"^--\s*sellora:([a-z-]+)\s*(.*)$")]
    private static partial Regex HeaderLine();

    public static (IReadOnlyList<SqlScript> Scripts, IReadOnlyList<string> Errors) Load(string root)
    {
        var scripts = new List<SqlScript>();
        var errors = new List<string>();
        if (!Directory.Exists(root))
        {
            errors.Add($"Scripts folder not found: {root}");
            return (scripts, errors);
        }

        LoadFolder(Path.Combine(root, "scripts"), ScriptKind.Data, DataName(), "yyyy.MM.dd.nnn__kebab-description.sql", scripts, errors);
        LoadFolder(Path.Combine(root, "repeatable"), ScriptKind.Repeatable, RepeatableName(), "R__kebab-description.sql", scripts, errors);
        LoadFolder(Path.Combine(root, "validation"), ScriptKind.Validation, ValidationName(), "V__kebab-description.sql", scripts, errors);
        return (scripts, errors);
    }

    private static void LoadFolder(string folder, ScriptKind kind, Regex namePattern, string expected,
        List<SqlScript> scripts, List<string> errors)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var path in Directory.EnumerateFiles(folder).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith('.')) continue; // .gitkeep, macOS .DS_Store
            if (!namePattern.IsMatch(name))
            {
                errors.Add($"{name}: name must look like {expected}");
                continue;
            }
            var script = Parse(name, kind, File.ReadAllText(path), errors);
            if (script is not null) scripts.Add(script);
        }
    }

    internal static SqlScript? Parse(string name, ScriptKind kind, string content, List<string> errors)
    {
        // Line endings differ between checkouts (macOS/Linux vs Windows); the checksum must not.
        var sql = content.TrimStart('﻿').Replace("\r\n", "\n");
        var errorCount = errors.Count;
        string? description = null;
        var timeout = DefaultTimeout;
        var lockTimeout = DefaultLockTimeout;
        var useTransaction = true;
        ViolationMode? onViolation = null;

        var lines = sql.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('\\'))
            {
                errors.Add($"{name}:{i + 1}: psql meta-commands ({line.Split(' ')[0]}) are not supported; use plain SQL");
                continue;
            }

            var header = HeaderLine().Match(line);
            if (!header.Success) continue;
            var key = header.Groups[1].Value;
            var value = header.Groups[2].Value.Trim();
            switch (key)
            {
                case "description":
                    description = value;
                    break;
                case "timeout":
                    timeout = ParseSeconds(name, i, key, value, timeout, errors);
                    break;
                case "lock-timeout":
                    lockTimeout = ParseSeconds(name, i, key, value, lockTimeout, errors);
                    break;
                case "transaction" when value is "per-script" or "none":
                    useTransaction = value == "per-script";
                    break;
                case "on-violation" when value is "warn" or "block":
                    onViolation = value == "warn" ? ViolationMode.Warn : ViolationMode.Block;
                    break;
                default:
                    errors.Add($"{name}:{i + 1}: unknown header '-- sellora:{key} {value}'");
                    break;
            }
        }

        if (kind == ScriptKind.Validation && !useTransaction)
            errors.Add($"{name}: validation scripts always run in a read-only transaction; remove 'transaction: none'");
        if (kind != ScriptKind.Validation && onViolation is not null)
            errors.Add($"{name}: 'on-violation' only applies to validation scripts");
        if (string.IsNullOrWhiteSpace(sql.Replace("\n", "")))
            errors.Add($"{name}: file is empty");

        if (errors.Count > errorCount) return null;
        return new SqlScript(name, kind, sql, Checksum(sql), description, timeout, lockTimeout, useTransaction, onViolation);
    }

    private static TimeSpan ParseSeconds(string name, int line, string key, string value, TimeSpan fallback, List<string> errors)
    {
        if (int.TryParse(value, out var seconds) && seconds > 0) return TimeSpan.FromSeconds(seconds);
        errors.Add($"{name}:{line + 1}: '{key}' must be a whole number of seconds, got '{value}'");
        return fallback;
    }

    public static string Checksum(string sql) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
}
