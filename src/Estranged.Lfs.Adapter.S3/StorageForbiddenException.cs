using Amazon.S3;
using System;

namespace Estranged.Lfs.Adapter.S3
{
    /// <summary>
    /// The object store answered 403 to a request signed with repository-scoped
    /// credentials inside the granted prefix. SeaweedFS has been seen to do this
    /// intermittently, so the caller retries with fresh credentials.
    /// </summary>
    public sealed class StorageForbiddenException : Exception
    {
        public StorageForbiddenException(AmazonS3Exception innerException)
            : base("Repository storage answered 403.", innerException)
        {
        }
    }
}
