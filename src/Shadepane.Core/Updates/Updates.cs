using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shadepane.Core.Updates;

/// <summary>Версия релиза вида 1.2.3 (тег v1.2.3). Предварительные версии не поддерживаются — их просто не предлагаем.</summary>
public sealed partial record ReleaseVersion(int Major, int Minor, int Patch) : IComparable<ReleaseVersion>
{
    [GeneratedRegex(@"^v?(\d{1,5})\.(\d{1,5})\.(\d{1,5})$")]
    private static partial Regex Pattern();

    public static ReleaseVersion? TryParse(string? text)
    {
        var m = Pattern().Match(text?.Trim() ?? "");
        return m.Success ? new(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
    }

    /// <summary>Из версии сборки: "0.2.0+abc123" или System.Version 0.2.0.0.</summary>
    public static ReleaseVersion? FromAssembly(string? informationalVersion)
    {
        var plus = informationalVersion?.IndexOf('+') ?? -1;
        return TryParse(plus >= 0 ? informationalVersion![..plus] : informationalVersion);
    }

    public bool IsDevelopmentBuild => this == new ReleaseVersion(0, 0, 0);

    public int CompareTo(ReleaseVersion? other) =>
        other is null ? 1 : (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);

public sealed record ReleaseInfo(string Tag, string PageUrl, bool IsDraft, bool IsPrerelease, IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>Ответ GET /repos/{owner}/{repo}/releases/latest. Некорректный ответ — null.</summary>
    public static ReleaseInfo? TryParse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var assets = new List<ReleaseAsset>();
            if (r.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in a.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : -1;
                    if (name is not null && url is not null)
                        assets.Add(new(name, url, size));
                }
            }
            return new ReleaseInfo(
                r.GetProperty("tag_name").GetString() ?? "",
                r.TryGetProperty("html_url", out var page) ? page.GetString() ?? "" : "",
                r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True,
                r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True,
                assets);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}

public sealed record UpdateCandidate(ReleaseVersion Version, string PageUrl, ReleaseAsset Installer, ReleaseAsset Checksums);

public static class UpdatePlanner
{
    public const string Owner = "aintlikeu";
    public const string Repository = "shadepane";
    public const string ChecksumsFile = "SHA256SUMS.txt";
    public static string LatestReleaseApi => $"https://api.github.com/repos/{Owner}/{Repository}/releases/latest";

    public static string InstallerName(ReleaseVersion v) => $"Shadepane-{v}-win-x64-setup.exe";

    /// <summary>
    /// Есть ли что ставить. Только опубликованный релиз (не черновик, не предварительный) новее текущей версии,
    /// с установщиком и SHA256SUMS.txt, скачиваемыми строго из релизов этого репозитория.
    /// </summary>
    public static UpdateCandidate? Select(ReleaseInfo? release, ReleaseVersion current)
    {
        if (release is null || release.IsDraft || release.IsPrerelease)
            return null;
        var version = ReleaseVersion.TryParse(release.Tag);
        if (version is null || version.CompareTo(current) <= 0)
            return null;
        var installer = release.Assets.FirstOrDefault(a => a.Name == InstallerName(version));
        var sums = release.Assets.FirstOrDefault(a => a.Name == ChecksumsFile);
        if (installer is null || sums is null || !IsOwnAssetUrl(installer, release.Tag) || !IsOwnAssetUrl(sums, release.Tag))
            return null;
        return new UpdateCandidate(version, release.PageUrl, installer, sums);
    }

    /// <summary>https://github.com/aintlikeu/shadepane/releases/download/&lt;tag&gt;/&lt;name&gt; — и ничего другого.</summary>
    public static bool IsOwnAssetUrl(ReleaseAsset asset, string tag)
    {
        if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return false;
        var expected = $"/{Owner}/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(asset.Name)}";
        return string.Equals(uri.AbsolutePath, expected, StringComparison.Ordinal);
    }
}

/// <summary>Файл SHA256SUMS.txt: строки "&lt;64 hex&gt;  &lt;имя файла&gt;" (формат sha256sum, допускается "*" перед именем).</summary>
public static partial class Sha256Sums
{
    [GeneratedRegex(@"^([0-9a-fA-F]{64}) [ *]?(\S.*)$")]
    private static partial Regex Line();

    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var m = Line().Match(raw.TrimEnd('\r').Trim());
            if (m.Success)
                result[m.Groups[2].Value.Trim()] = m.Groups[1].Value.ToLowerInvariant();
        }
        return result;
    }

    public static string Compute(Stream stream) => Convert.ToHexStringLower(SHA256.HashData(stream));

    public static bool Matches(Stream stream, string expectedHex) =>
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(Compute(stream)),
            System.Text.Encoding.ASCII.GetBytes(expectedHex.ToLowerInvariant()));
}
