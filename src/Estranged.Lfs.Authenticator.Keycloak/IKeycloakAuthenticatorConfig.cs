namespace Estranged.Lfs.Authenticator.Keycloak
{
    public interface IKeycloakAuthenticatorConfig
    {
        string RealmUrl { get; }
        string RequiredRole { get; }
        string ClientPrefix { get; }
    }
}
