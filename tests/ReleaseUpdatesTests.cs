using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AirDesk;
using Xunit;

public sealed class ReleaseUpdatesTests
{
    const string Latest = "https://api.github.com/repos/kolyased/AirDesk/releases/latest";
    const string Download = "https://release-assets.githubusercontent.com/installer";
    const string CurrentSigner = "current-publisher";
    static readonly Version Installed = new(0, 7, 1);
    static readonly Version Preview = new(0, 7, 2);
    static readonly byte[] Installer = File.ReadAllBytes(typeof(ReleaseUpdatesTests).Assembly.Location);
    static readonly Version InstallerVersion = Version.Parse(typeof(ReleaseUpdatesTests).Assembly.GetName().Version!.ToString(3));

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task LatestEndpointCannotOfferANewerDraftOrPrerelease(bool draft, bool prerelease)
    {
        using var http = WithResponses((Latest, Metadata(Preview, draft, prerelease)));

        Assert.Null(await ReleaseUpdates.CheckAsync(http, Installed, CancellationToken.None));
    }

    [Fact]
    public async Task MissingLatestStableReleaseProducesNoUpdate()
    {
        using var http = WithResponses((Latest, new HttpResponseMessage(HttpStatusCode.NotFound)));

        Assert.Null(await ReleaseUpdates.CheckAsync(http, Installed, CancellationToken.None));
    }

    [Fact]
    public async Task StableUpdateFollowsTrustedRedirectAndSavesVerifiedInstaller()
    {
        using var output = new TestDirectory();
        using var http = WithResponses((Latest, Metadata(InstallerVersion)), (Asset(InstallerVersion), Redirect(Download)),
            (Download, Response(Installer)));
        var package = await Check(http);

        Assert.Equal(InstallerVersion, package.Version);
        await Save(package, output.Path, http);

        Assert.Equal(Installer, await File.ReadAllBytesAsync(output.Path, CancellationToken.None));
        Assert.Equal(output.Path, Assert.Single(Directory.GetFiles(output.DirectoryPath)));
    }

    [Fact]
    public async Task TamperedBytesAreRejectedBeforeInstallerSignatureAndRemoved()
    {
        using var output = new TestDirectory();
        byte[] altered = (byte[])Installer.Clone();
        altered[^1] ^= 1;
        using var http = WithResponses((Latest, Metadata(InstallerVersion)), (Asset(InstallerVersion), Response(altered)));
        var package = await Check(http);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Save(package, output.Path, http, path =>
        {
            Assert.Equal(typeof(ReleaseUpdatesTests).Assembly.Location, path);
            return CurrentSigner;
        }));

        Assert.Contains("SHA-256", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(output.DirectoryPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("different-publisher")]
    public async Task InstallerRequiresAValidSignatureFromTheInstalledPublisher(string? updateSigner)
    {
        using var output = new TestDirectory();
        using var http = WithResponses((Latest, Metadata(InstallerVersion)), (Asset(InstallerVersion), Response(Installer)));
        var package = await Check(http);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Save(package, output.Path, http,
            path => path == typeof(ReleaseUpdatesTests).Assembly.Location ? CurrentSigner : updateSigner));

        Assert.Contains("издателя", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(output.DirectoryPath));
    }

    [Fact]
    public async Task UnsignedInstalledApplicationCannotAuthorizeADownload()
    {
        using var output = new TestDirectory();
        using var http = WithResponses((Latest, Metadata(InstallerVersion)));
        var package = await Check(http);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Save(package, output.Path, http, _ => null));

        Assert.Contains("не имеет действительной подписи", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(output.DirectoryPath));
    }

    [Fact]
    public async Task TrustedInstallerWithMismatchedVersionIsRejectedAndRemoved()
    {
        using var output = new TestDirectory();
        var differentVersion = new Version(InstallerVersion.Major, InstallerVersion.Minor, InstallerVersion.Build + 1);
        using var http = WithResponses((Latest, Metadata(differentVersion)), (Asset(differentVersion), Response(Installer)));
        var package = await Check(http);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Save(package, output.Path, http));

        Assert.Contains("Версия подписанного установщика", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(output.DirectoryPath));
    }

    [Fact]
    public async Task UnknownRedirectIsRejectedBeforeContactingIt()
    {
        using var output = new TestDirectory();
        using var http = WithResponses((Latest, Metadata(InstallerVersion)),
            (Asset(InstallerVersion), Redirect("https://example.com/installer.exe")));
        var package = await Check(http);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Save(package, output.Path, http));

        Assert.Contains("неизвестный адрес", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(output.DirectoryPath));
    }

    static async Task<ReleasePackage> Check(HttpClient http) =>
        Assert.IsType<ReleasePackage>(await ReleaseUpdates.CheckAsync(http, new Version(0, 0, 0), CancellationToken.None));

    static Task Save(ReleasePackage package, string destination, HttpClient http, Func<string, string?>? signer = null) =>
        ReleaseUpdates.SaveVerifiedAsync(package, destination, typeof(ReleaseUpdatesTests).Assembly.Location, http,
            (path, _) => Task.FromResult(signer == null ? CurrentSigner : signer(path)), CancellationToken.None);

    static string Asset(Version version) => $"https://github.com/kolyased/AirDesk/releases/download/v{version}/AirDesk-{version}-Setup-win-x64.exe";

    static HttpResponseMessage Metadata(Version version, bool draft = false, bool prerelease = false) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            draft, prerelease, tag_name = $"v{version}", assets = new[]
            {
                new
                {
                    name = $"AirDesk-{version}-Setup-win-x64.exe", browser_download_url = Asset(version), state = "uploaded",
                    size = Installer.Length, digest = $"sha256:{Convert.ToHexString(SHA256.HashData(Installer))}"
                }
            }
        }))
    };

    static HttpResponseMessage Response(byte[] content) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };

    static HttpResponseMessage Redirect(string target) => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri(target) } };

    static HttpClient WithResponses(params (string Url, HttpResponseMessage Response)[] responses) => new(new TestTransport(responses));

    sealed class TestTransport((string Url, HttpResponseMessage Response)[] responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            foreach (var route in responses)
            {
                if (request.RequestUri?.AbsoluteUri == route.Url) return Task.FromResult(route.Response);
            }
            throw new InvalidOperationException($"Unexpected request to {request.RequestUri}.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                foreach (var route in responses) route.Response.Dispose();
            base.Dispose(disposing);
        }
    }

    sealed class TestDirectory : IDisposable
    {
        readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("airdesk-update-");

        public string DirectoryPath => directory.FullName;
        public string Path => System.IO.Path.Combine(directory.FullName, "setup.exe");
        public void Dispose() => directory.Delete(recursive: true);
    }
}
