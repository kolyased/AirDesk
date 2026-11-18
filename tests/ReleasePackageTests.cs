using System;
using System.IO;
using System.Text.Json;
using AirDesk;
using Xunit;

public sealed class ReleasePackageTests
{
    const string Digest = "sha256:0000000000000000000000000000000000000000000000000000000000000000";
    const string Asset = "https://github.com/kolyased/AirDesk/releases/download/v0.7.0/AirDesk-0.7.0-Setup-win-x64.exe";

    [Fact]
    public void StableReleaseMustBeNewerAndMatchBytes()
    {
        var installed = new Version(0, 6, 0, 0);
        using var valid = JsonDocument.Parse(Build(Digest, Asset));
        var release = ReleasePackage.Parse(valid.RootElement, installed);
        Verify(release is { Size: 1024, FileName: "AirDesk-0.7.0-Setup-win-x64.exe" } && release.Version == new Version(0, 7, 0), "recognizes a stable update");
        Verify(release!.Matches(1024, new byte[32]), "accepts matching bytes");
        Verify(!release.Matches(1023, new byte[32]), "rejects a truncated download");
        var changed = new byte[32];
        changed[0] = 1;
        Verify(!release.Matches(1024, changed), "rejects differing digest");
        Verify(ReleasePackage.Parse(valid.RootElement, new Version(0, 7, 0, 0)) == null, "ignores installed version");
        Verify(ReleasePackage.Parse(valid.RootElement, new Version(1, 0, 0)) == null, "rejects implicit downgrade");
    }

    [Fact]
    public void PreviewsAndDraftReleasesAreIgnored()
    {
        var installed = new Version(0, 6, 0, 0);
        using var valid = JsonDocument.Parse(Build(Digest, Asset));
        using var prerelease = JsonDocument.Parse(Build(Digest, Asset, prerelease: true));
        Verify(ReleasePackage.Parse(prerelease.RootElement, installed) == null, "ignores prereleases");
        using var draft = JsonDocument.Parse(Build(Digest, Asset, draft: true));
        Verify(ReleasePackage.Parse(draft.RootElement, installed) == null, "ignores drafts");
    }

    [Fact]
    public void PackageOriginSizeAndDigestMustMatch()
    {
        var installed = new Version(0, 6, 0, 0);
        Verify(Rejected(Build("sha256:xyz", Asset), installed), "requires a complete digest");
        Verify(Rejected(Build(Digest, "https://example.org/setup.exe"), installed), "rejects arbitrary locations");
        Verify(Rejected(Build(Digest, Asset, size: ReleasePackage.MaxPackageBytes + 1), installed), "bounds downloads");
    }

    [Fact]
    public void RedirectsRequireTrustedHttpsHost()
    {
        Verify(ReleasePackage.AllowsRedirect(new Uri("https://release-assets.githubusercontent.com/path")), "permits GitHub release CDN");
        Verify(!ReleasePackage.AllowsRedirect(new Uri("https://release-assets.githubusercontent.com.example.org/setup")), "rejects misleading hosts");
        Verify(!ReleasePackage.AllowsRedirect(new Uri("http://release-assets.githubusercontent.com/path")), "requires TLS");
    }

    static string Build(string digest, string url, long size = 1024, bool prerelease = false, bool draft = false) =>
        JsonSerializer.Serialize(new { draft, prerelease, tag_name = "v0.7.0", assets = new[]
        {
            new { name = "AirDesk-0.7.0-Setup-win-x64.exe", browser_download_url = url, state = "uploaded", size, digest }
        } });

    static bool Rejected(string json, Version installed)
    {
        using var document = JsonDocument.Parse(json);
        try { ReleasePackage.Parse(document.RootElement, installed); }
        catch (InvalidDataException) { return true; }
        return false;
    }

    static void Verify(bool result, string check)
    {
        Assert.True(result, check);
    }
}
