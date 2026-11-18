using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AirDesk;

internal static class ReleaseUpdates
{
    static readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
    public static Version Installed => typeof(ReleaseUpdates).Assembly.GetName().Version!;
    public static string VersionLabel => typeof(ReleaseUpdates).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Installed.ToString(3);

    public static async Task<ReleasePackage?> CheckAsync(CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{ReleasePackage.Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("AirDesk/" + Installed.ToString(3));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var source = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        var buffer = new byte[512 * 1024 + 1];
        int read = await source.ReadAtLeastAsync(buffer, buffer.Length, false, cancel).ConfigureAwait(false);
        if (read == buffer.Length) throw new InvalidDataException("Ответ GitHub превышает допустимый размер.");
        using var document = JsonDocument.Parse(buffer.AsMemory(0, read));
        return ReleasePackage.Parse(document.RootElement, Installed);
    }

    public static async Task SaveVerifiedAsync(ReleasePackage package, string destination, CancellationToken cancel)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь к AirDesk.exe.");
        var signer = await VerifiedSignerAsync(executable, cancel).ConfigureAwait(false);
        if (signer == null)
            throw new InvalidOperationException("Эта копия AirDesk не имеет действительной подписи. Приложение не может установить доверие к обновлению.");

        string temp = Path.Combine(Path.GetDirectoryName(destination)!, Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            using var response = await GetAssetAsync(package.Url, cancel).ConfigureAwait(false);
            using var source = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                int count;
                long size = 0;
                var buffer = new byte[65536];
                while ((count = await source.ReadAsync(buffer.AsMemory(), cancel).ConfigureAwait(false)) != 0)
                {
                    size += count;
                    if (size > package.Size) throw new InvalidDataException("Размер установщика превышает опубликованный.");
                    sha.AppendData(buffer, 0, count);
                    await file.WriteAsync(buffer.AsMemory(0, count), cancel).ConfigureAwait(false);
                }
                if (!package.Matches(size, sha.GetHashAndReset())) throw new InvalidDataException("SHA-256 установщика не совпадает с релизом.");
            }
            string? updateSigner = await VerifiedSignerAsync(temp, cancel).ConfigureAwait(false);
            if (!string.Equals(signer, updateSigner, StringComparison.Ordinal))
                throw new InvalidDataException("Windows не подтвердила подпись того же издателя.");
            var info = FileVersionInfo.GetVersionInfo(temp);
            if (new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart) != package.Version)
                throw new InvalidDataException("Версия подписанного установщика не совпадает с версией релиза.");
            File.Move(temp, destination);
        }
        finally { File.Delete(temp); }
    }

    static async Task<HttpResponseMessage> GetAssetAsync(Uri url, CancellationToken cancel)
    {
        for (int hop = 0; hop < 4; hop++)
        {
            var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return response;
            using (response)
            {
                if ((int)response.StatusCode is < 300 or >= 400) response.EnsureSuccessStatusCode();
                var location = response.Headers.Location;
                if (location == null || !ReleasePackage.AllowsRedirect(location))
                    throw new InvalidDataException("GitHub перенаправил загрузку на неизвестный адрес.");
                url = location;
            }
        }
        throw new InvalidDataException("Превышен лимит перенаправлений GitHub.");
    }

    static async Task<string?> VerifiedSignerAsync(string file, CancellationToken cancel)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var start = new ProcessStartInfo(Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$s=Microsoft.PowerShell.Security\\Get-AuthenticodeSignature -LiteralPath $env:AIRDESK_SIGNATURE_FILE; " +
            "if($s.Status -eq 'Valid' -and $s.SignerCertificate){" +
            "[BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($s.SignerCertificate.RawData)).Replace('-','')}");
        start.Environment["AIRDESK_SIGNATURE_FILE"] = file;
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить проверку подписи Windows.");
        using var stop = cancel.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        string result = (await process.StandardOutput.ReadToEndAsync(cancel).ConfigureAwait(false)).Trim();
        await process.WaitForExitAsync(cancel).ConfigureAwait(false);
        return process.ExitCode == 0 && result.Length == 64 &&
            Convert.FromHexString(result, new byte[32], out _, out _) == OperationStatus.Done ? result : null;
    }
}
