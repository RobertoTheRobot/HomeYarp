using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HomeYarp.Tests.TestHelpers;
using HomeYarp.WebServer.Dtos;
using HomeYarp.WebServer.RootCa;

namespace HomeYarp.Tests.WebServer.RootCa;

public class RootCaInstallScriptsTests
{
    private static async Task<RootCaResponse> NewCaAsync()
        => RootCaResponse.From(await InMemoryRootCaStore.NewService().EnsureInitializedAsync());

    [Fact]
    public async Task RootCaResponse_Fingerprint_IsSha256OfDerCertificate()
    {
        var ca = await NewCaAsync();
        using var cert = X509Certificate2.CreateFromPem(ca.CertificatePem);

        ca.Sha256Fingerprint.ShouldBe(Convert.ToHexString(SHA256.HashData(cert.RawData)));
    }

    [Fact]
    public async Task WindowsPowerShell_EmbedsCertAndFingerprint_AndNeverAPrivateKey()
    {
        var ca = await NewCaAsync();

        var script = RootCaInstallScripts.WindowsPowerShell(ca);

        script.ShouldContain($"$expected = '{ca.Sha256Fingerprint}'");
        script.ShouldContain("-----BEGIN CERTIFICATE-----");
        script.ShouldContain("X509Store('Root', 'CurrentUser')");
        script.ShouldNotContain("PRIVATE");
        script.ShouldContain("\n'@\n"); // here-string terminator must sit at column 0
    }

    [Fact]
    public async Task LinuxMacSh_EmbedsCertAndFingerprint_UsesLfOnly_AndHeredocTerminatorAtColumnZero()
    {
        var ca = await NewCaAsync();

        var script = RootCaInstallScripts.LinuxMacSh(ca);

        script.ShouldStartWith("#!/bin/sh\n");
        script.ShouldContain($"EXPECTED=\"{ca.Sha256Fingerprint}\"");
        script.ShouldNotContain("\r");
        script.ShouldNotContain("PRIVATE");
        script.Split('\n').ShouldContain("HOMEYARP_CA_PEM");
        script.ShouldContain("-----END CERTIFICATE-----\nHOMEYARP_CA_PEM\n");
    }

    [Fact]
    public async Task LinuxMacSh_PassesShellSyntaxCheck()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh"))
        {
            return;
        }
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "install.sh");
        await File.WriteAllTextAsync(path, RootCaInstallScripts.LinuxMacSh(await NewCaAsync()));

        using var proc = Process.Start(new ProcessStartInfo("/bin/sh", $"-n \"{path}\"") { RedirectStandardError = true })!;
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        proc.ExitCode.ShouldBe(0, stderr);
    }

    [Fact]
    public void WindowsCmd_UsesCrlf_AndFetchesTheInstallerFromTheSameInstance()
    {
        var cmd = RootCaInstallScripts.WindowsCmd("http://192.168.1.2:5269");

        cmd.ShouldContain("\r\n");
        cmd.Replace("\r\n", string.Empty).ShouldNotContain("\n");
        cmd.ShouldContain("http://192.168.1.2:5269/api/ca/install/windows.ps1");
        cmd.ShouldContain("pause");
    }

    [Theory]
    [InlineData("http", "192.168.1.2:5269", "http://192.168.1.2:5269")]
    [InlineData("https", "homeyarp.lab", "https://homeyarp.lab")]
    [InlineData("http", "evil.com'; Remove-Item C:\\ -Recurse; '", "http://HOMEYARP-HOST:5269")]
    [InlineData("http", "a b", "http://HOMEYARP-HOST:5269")]
    [InlineData("javascript", "host", "http://HOMEYARP-HOST:5269")]
    [InlineData("http", "host&calc", "http://HOMEYARP-HOST:5269")]
    public void SafeBaseUrl_AcceptsPlainHostsAndRejectsAnythingThatCouldInjectIntoAScript(string scheme, string host, string expected)
        => RootCaInstallScripts.SafeBaseUrl(scheme, host).ShouldBe(expected);
}
