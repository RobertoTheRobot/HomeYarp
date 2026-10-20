using System.Text;
using System.Text.RegularExpressions;
using HomeYarp.WebServer.Dtos;

namespace HomeYarp.WebServer.RootCa;

/// <summary>
/// Generates small self-contained installers for the root CA. Each script embeds the PUBLIC certificate and
/// refuses to install unless the embedded certificate's SHA-256 matches the fingerprint shown in the UI.
/// The private key is never involved. Everything interpolated into a script is either base64/hex we generated
/// or a host string validated by <see cref="SafeBaseUrl"/>.
/// </summary>
public static partial class RootCaInstallScripts
{
    [GeneratedRegex(@"^[A-Za-z0-9.\-]+(:\d{1,5})?$")]
    private static partial Regex HostPattern();

    /// <summary>Builds scheme://host[:port] from a request, falling back to a placeholder if the Host header looks hostile.</summary>
    public static string SafeBaseUrl(string scheme, string host)
        => (scheme is "http" or "https") && HostPattern().IsMatch(host) ? $"{scheme}://{host}" : "http://HOMEYARP-HOST:5269";

    public static string WindowsPowerShell(RootCaResponse ca)
    {
        var sb = new StringBuilder();
        sb.Append("# HomeYarp root CA installer (current user, no admin needed).\n");
        sb.Append("# Verifies the embedded certificate against the fingerprint shown on the HomeYarp 'Install CA' page, then trusts it.\n");
        sb.Append("$ErrorActionPreference = 'Stop'\n");
        sb.Append($"$expected = '{ca.Sha256Fingerprint}'\n");
        sb.Append("$pem = @'\n").Append(Pem(ca)).Append("'@\n");
        sb.Append("""
            $der  = [Convert]::FromBase64String(($pem -replace '-----[^-]+-----', '' -replace '\s', ''))
            $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 (,$der)
            $sha  = [System.Security.Cryptography.SHA256]::Create()
            $actual = ([BitConverter]::ToString($sha.ComputeHash($cert.RawData))) -replace '-', ''
            if ($actual -ne $expected) { throw "Fingerprint mismatch (got $actual) - NOT installing. Re-download from the HomeYarp page." }
            Write-Host "HomeYarp root CA: $($cert.Subject)"
            Write-Host "SHA-256: $actual"
            $store = New-Object System.Security.Cryptography.X509Certificates.X509Store('Root', 'CurrentUser')
            $store.Open('ReadWrite')
            try {
                foreach ($old in @($store.Certificates | Where-Object { $_.Subject -eq $cert.Subject -and $_.Thumbprint -ne $cert.Thumbprint })) { $store.Remove($old) }
                if ($store.Certificates | Where-Object { $_.Thumbprint -eq $cert.Thumbprint }) { Write-Host 'Already trusted - nothing to do.' -ForegroundColor Green }
                else { $store.Add($cert); Write-Host 'Installed. Restart your browser, then use the "Check this device" button.' -ForegroundColor Green }
            } finally { $store.Close() }

            """);
        return sb.ToString();
    }

    /// <summary>Double-click wrapper: runs the PowerShell installer served by the same HomeYarp instance.</summary>
    public static string WindowsCmd(string baseUrl)
        => string.Join("\r\n",
            "@echo off",
            "title HomeYarp root CA installer",
            "echo Installing the HomeYarp root CA for the current user...",
            $"powershell -NoProfile -ExecutionPolicy Bypass -Command \"Invoke-RestMethod '{baseUrl}/api/ca/install/windows.ps1' | Invoke-Expression\"",
            "echo.",
            "pause",
            "");

    public static string LinuxMacSh(RootCaResponse ca)
        => $$"""
            #!/bin/sh
            # HomeYarp root CA installer for Linux and macOS.
            # Verifies the embedded certificate against the fingerprint shown on the HomeYarp 'Install CA' page,
            # then trusts it system-wide and in Chrome/Chromium/Firefox profiles (when NSS certutil is available).
            # Set DRY_RUN=1 to print what would be done without changing anything.
            set -eu
            EXPECTED="{{ca.Sha256Fingerprint}}"
            NICK="{{ca.Subject.Replace("CN=", string.Empty)}}"
            TMP=$(mktemp)
            trap 'rm -f "$TMP"' EXIT
            cat > "$TMP" <<'HOMEYARP_CA_PEM'
            {{Pem(ca).TrimEnd('\n')}}
            HOMEYARP_CA_PEM

            command -v openssl >/dev/null 2>&1 || { echo "openssl is required to verify the certificate." >&2; exit 1; }
            ACTUAL=$(openssl x509 -in "$TMP" -noout -fingerprint -sha256 | cut -d= -f2 | tr -d ':' | tr 'a-f' 'A-F')
            if [ "$ACTUAL" != "$EXPECTED" ]; then
              echo "Fingerprint mismatch (got $ACTUAL) - NOT installing. Re-download from the HomeYarp page." >&2
              exit 1
            fi
            echo "HomeYarp root CA: $NICK"
            echo "SHA-256: $ACTUAL"

            run() { if [ -n "${DRY_RUN:-}" ]; then echo "[dry-run] $*"; else "$@"; fi; }
            SUDO=""; [ "$(id -u)" -eq 0 ] || SUDO="sudo"

            if [ "$(uname -s)" = "Darwin" ]; then
              run $SUDO security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain "$TMP"
            elif command -v update-ca-certificates >/dev/null 2>&1; then
              run $SUDO cp "$TMP" /usr/local/share/ca-certificates/homeyarp-root-ca.crt
              run $SUDO update-ca-certificates
            elif command -v update-ca-trust >/dev/null 2>&1; then
              run $SUDO cp "$TMP" /etc/pki/ca-trust/source/anchors/homeyarp-root-ca.crt
              run $SUDO update-ca-trust
            elif command -v trust >/dev/null 2>&1; then
              run $SUDO trust anchor --store "$TMP"
            else
              echo "Could not find a system trust tool (update-ca-certificates / update-ca-trust / trust)." >&2
            fi

            # Browsers with their own NSS databases (Chrome/Chromium/Edge: ~/.pki/nssdb, Firefox: profile dirs).
            if command -v certutil >/dev/null 2>&1; then
              for db in "$HOME/.pki/nssdb" "$HOME"/snap/chromium/current/.pki/nssdb \
                        "$HOME"/.mozilla/firefox/*/ "$HOME"/snap/firefox/common/.mozilla/firefox/*/ ; do
                [ -f "$db/cert9.db" ] || continue
                echo "Trusting in browser database: $db"
                run certutil -d "sql:$db" -D -n "$NICK" >/dev/null 2>&1 || true
                run certutil -d "sql:$db" -A -t "C,," -n "$NICK" -i "$TMP"
              done
            else
              echo "Note: install NSS tools (libnss3-tools / nss-tools) and re-run to also trust Chrome/Firefox profiles."
            fi
            echo "Done. Restart your browser, then use the 'Check this device' button on the HomeYarp page."
            """.ReplaceLineEndings("\n") + "\n";

    private static string Pem(RootCaResponse ca) => ca.CertificatePem.ReplaceLineEndings("\n").TrimEnd('\n') + "\n";
}
