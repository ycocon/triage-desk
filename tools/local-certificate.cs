using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

var directory = Directory.CreateDirectory(args.Length == 0 ? ".local" : args[0]).FullName;
var certificatePath = Path.Combine(directory, "localhost.pem");
var keyPath = Path.Combine(directory, "localhost.key");
var caPath = Path.Combine(directory, "localhost-ca.pem");
var starts = DateTimeOffset.UtcNow.AddMinutes(-5);
var expires = DateTimeOffset.UtcNow.AddDays(30);
using var issuerKey = RSA.Create(2048);
var issuerRequest = new CertificateRequest("CN=Triage Desk Local Development", issuerKey,
    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
issuerRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, critical: true));
issuerRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, critical: true));
using var issuer = issuerRequest.CreateSelfSigned(starts, expires);

using var key = RSA.Create(2048);
var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
var host = new SubjectAlternativeNameBuilder();
host.AddDnsName("localhost");
host.AddIpAddress(System.Net.IPAddress.Loopback);
request.CertificateExtensions.Add(host.Build());
request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
using var certificate = request.Create(issuer, starts, expires, RandomNumberGenerator.GetBytes(16));
File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
File.WriteAllText(caPath, issuer.ExportCertificatePem());
if (!OperatingSystem.IsWindows())
    File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
Console.WriteLine($"Local HTTPS certificate, key and CA certificate written to {directory}.");
