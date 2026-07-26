namespace WebPass.Web.Application.Secrets;

public interface IDataKeyWrapper
{
    string CurrentCertificateThumbprint { get; }

    byte[] WrapKey(ReadOnlySpan<byte> dataKey);

    byte[] UnwrapKey(ReadOnlySpan<byte> wrappedKey, string certificateThumbprint);
}
