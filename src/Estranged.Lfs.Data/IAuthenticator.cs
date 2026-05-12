using System.Threading;
using System.Threading.Tasks;

namespace Estranged.Lfs.Data
{
    public interface IAuthenticator
    {
        Task Authenticate(string username, string password, string organisation, string repository, LfsPermission requiredPermission, CancellationToken token);
    }
}
