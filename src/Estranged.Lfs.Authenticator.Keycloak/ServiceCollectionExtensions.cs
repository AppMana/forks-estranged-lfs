using Estranged.Lfs.Data;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http;

namespace Estranged.Lfs.Authenticator.Keycloak
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddLfsKeycloakAuthenticator(this IServiceCollection services, IKeycloakAuthenticatorConfig config)
        {
            return services
                .AddSingleton(config)
                .AddSingleton(new HttpClient())
                .AddSingleton<IAuthenticator, KeycloakClientCredentialsAuthenticator>();
        }
    }
}
