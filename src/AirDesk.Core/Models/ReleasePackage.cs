using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace AirDesk;

internal sealed record ReleasePackage(Version Version, string FileName, Uri Url, long Size, byte[] Sha256)
{
    public const string Repository = "kolyased/AirDesk";
    public const long MaxPackageBytes = 200 * 1024 * 1024;

    public static ReleasePackage? Parse(JsonElement release, Version installed)
    {
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        string? tag = release.GetProperty("tag_name").GetString();
        if (tag == null || !tag.StartsWith('v') || !Version.TryParse(tag[1..], out var version) ||
            version.Build < 0 || version.Revision >= 0 || tag != $"v{version.ToString(3)}")
            throw new InvalidDataException("Неизвестный формат версии AirDesk.");
        if (version <= new Version(installed.Major, installed.Minor, Math.Max(0, installed.Build))) return null;

        string fileName = $"AirDesk-{version.ToString(3)}-Setup-win-x64.exe";
        string url = $"https://github.com/{Repository}/releases/download/{tag}/{fileName}";
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != fileName) continue;
            string? digest = asset.TryGetProperty("digest", out var value) ? value.GetString() : null;
            long size = asset.GetProperty("size").GetInt64();
            if (asset.GetProperty("state").GetString() != "uploaded" || asset.GetProperty("browser_download_url").GetString() != url ||
                size is <= 0 or > MaxPackageBytes || digest?.Length != 71 || !digest.StartsWith("sha256:", StringComparison.Ordinal))
                break;
            var hash = new byte[32];
            if (Convert.FromHexString(digest.AsSpan(7), hash, out _, out int written) == OperationStatus.Done && written == hash.Length)
                return new(version, fileName, new Uri(url), size, hash);
            break;
        }
        throw new InvalidDataException("Для новой версии отсутствует установщик с проверяемой контрольной суммой.");
    }

    public static bool AllowsRedirect(Uri target) => target.IsAbsoluteUri && target.Scheme == Uri.UriSchemeHttps && target.IsDefaultPort &&
        target.UserInfo.Length == 0 && target.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com";

    public bool Matches(long size, byte[] digest) => Size == size && CryptographicOperations.FixedTimeEquals(Sha256, digest);
}
