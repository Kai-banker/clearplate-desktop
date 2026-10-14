using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ClearPlate.Core;

public sealed class CaptureCertificates : IDisposable
{
    public X509Certificate2 Root { get; }
    public X509Certificate2 Server { get; }
    public CaptureCertificates(DateTimeOffset? start = null)
    {
        var now = start ?? DateTimeOffset.UtcNow;
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=ClearPlate temporary login capture " + Guid.NewGuid().ToString("N"), rootKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        // Critical name constraint: this root can authorize only the ClearPlate API hostname.
        var constraints = new AsnWriter(AsnEncodingRules.DER);
        constraints.PushSequence();
        var permitted = new Asn1Tag(TagClass.ContextSpecific, 0, true);
        constraints.PushSequence(permitted);
        constraints.PushSequence();
        constraints.WriteCharacterString(UniversalTagNumber.IA5String, CaptureProtocol.ApiHost, new Asn1Tag(TagClass.ContextSpecific, 2));
        constraints.PopSequence(); constraints.PopSequence(permitted); constraints.PopSequence();
        request.CertificateExtensions.Add(new X509Extension("2.5.29.30", constraints.Encode(), true));
        Root = request.CreateSelfSigned(now.AddMinutes(-1), now.AddMinutes(6));
        using var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var serverRequest = new CertificateRequest("CN=" + CaptureProtocol.ApiHost, serverKey, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(CaptureProtocol.ApiHost);
        serverRequest.CertificateExtensions.Add(names.Build());
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        using var issued = serverRequest.Create(Root, now.AddMinutes(-1), now.AddMinutes(5), RandomNumberGenerator.GetBytes(16));
        Server = issued.CopyWithPrivateKey(serverKey);
    }
    public void Dispose() { Server.Dispose(); Root.Dispose(); }
}
