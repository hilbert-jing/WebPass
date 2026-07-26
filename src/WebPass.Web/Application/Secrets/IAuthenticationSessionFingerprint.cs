namespace WebPass.Web.Application.Secrets;

public interface IAuthenticationSessionFingerprint
{
    string GetCurrent();
}
