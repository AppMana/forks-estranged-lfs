using Estranged.Lfs.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Estranged.Lfs.Api
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddLfsApi(this IServiceCollection services)
        {
            services.AddMvcCore()
                    .AddGitLfs();

            services.TryAddSingleton(new LfsBatchOptions());
            services.AddLfsData();
            return services;
        }
    }
}
