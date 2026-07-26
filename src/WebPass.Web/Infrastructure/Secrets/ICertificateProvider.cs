using System.Security.Cryptography.X509Certificates;

namespace WebPass.Web.Infrastructure.Secrets;

public interface ICertificateProvider
{
    X509Certificate2 GetByThumbprint(string thumbprint, bool requirePrivateKey);
}
