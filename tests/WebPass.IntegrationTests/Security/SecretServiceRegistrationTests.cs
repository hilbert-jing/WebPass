using Microsoft.Extensions.DependencyInjection;
using WebPass.Web.Application.Secrets;
using WebPass.Web.Infrastructure.Secrets;
using Xunit;

namespace WebPass.IntegrationTests.Security;

public sealed class SecretServiceRegistrationTests
{
    [Fact]
    public void Application_registers_secret_services_without_opening_the_certificate_store()
    {
        using var factory = new WebPassFactory();
        using var scope = factory.Services.CreateScope();

        Assert.IsType<AesGcmSecretCipher>(scope.ServiceProvider.GetRequiredService<ISecretCipher>());
        Assert.IsType<DatabaseDataEncryptionKeyProvider>(
            scope.ServiceProvider.GetRequiredService<IDataEncryptionKeyProvider>());
        Assert.IsType<CertificateKeyWrapper>(scope.ServiceProvider.GetRequiredService<IDataKeyWrapper>());
        Assert.IsType<DataKeyRotationService>(
            scope.ServiceProvider.GetRequiredService<DataKeyRotationService>());
    }
}
