using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace Estranged.Lfs.Data
{
    public sealed class FallbackBlobAdapter : IBlobAdapter
    {
        private readonly IBlobAdapter primary;
        private readonly IReadOnlyList<IBlobAdapter> fallbacks;

        public FallbackBlobAdapter(IBlobAdapter primary, IBlobAdapter fallback)
            : this(primary, new[] { fallback })
        {
        }

        public FallbackBlobAdapter(IBlobAdapter primary, IEnumerable<IBlobAdapter> fallbacks)
        {
            this.primary = primary;
            this.fallbacks = fallbacks.ToList();
        }

        public Task<SignedBlob> UriForUpload(string oid, long size, CancellationToken token)
        {
            return primary.UriForUpload(oid, size, token);
        }

        public async Task<SignedBlob> UriForDownload(string oid, CancellationToken token)
        {
            var signedBlob = await primary.UriForDownload(oid, token).ConfigureAwait(false);
            foreach (var fallback in fallbacks)
            {
                if (signedBlob.ErrorCode != 404)
                {
                    return signedBlob;
                }
                signedBlob = await fallback.UriForDownload(oid, token).ConfigureAwait(false);
            }

            return signedBlob;
        }
    }
}
