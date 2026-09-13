using System.Threading;
using System.Threading.Tasks;
namespace Estranged.Lfs.Data
{
    public interface IRepositoryAuthenticator
    {
        Task Authenticate(string authorization, string repositoryId, LfsPermission permission, CancellationToken token);
    }
}
