namespace Estranged.Lfs.Authenticator.Keycloak
{
    public sealed class KeycloakAuthenticatorConfig : IKeycloakAuthenticatorConfig
    {
        public string RealmUrl { get; set; }
        public string RequiredRole { get; set; } = "lfs";
        public string ClientPrefix { get; set; } = "git-lfs-";
    }
}
