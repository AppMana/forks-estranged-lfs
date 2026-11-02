using System;

namespace Estranged.Lfs.Adapter.S3
{
    public sealed class S3BlobAdapterConfig : IS3BlobAdapterConfig
    {
        public string Bucket { get; set; }
        public string KeyPrefix { get; set; }
        public string SharedKeyPrefix { get; set; }
        public bool DeduplicateUploads { get; set; }
        public TimeSpan Expiry { get; set; } = TimeSpan.FromHours(1);
        // Upload deduplication is an optimisation inside the batch request
        // budget: past this bound the client is given a presigned upload.
        public TimeSpan DeduplicationTimeout { get; set; } = TimeSpan.FromSeconds(2);
    }
}
