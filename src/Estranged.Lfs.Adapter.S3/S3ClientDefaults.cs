using Amazon.S3;

namespace Estranged.Lfs.Adapter.S3
{
    public static class S3ClientDefaults
    {
        /// <summary>
        /// The SDK's default of four retries backs off 0.4 s, 1.6 s, 6.4 s and
        /// 25.6 s, so a single call to a struggling object store could outlast
        /// the batch request budget. Two SDK retries (2 s of backoff) leave room
        /// for the adapter's own bounded retries inside the budget.
        /// </summary>
        public const int MaxErrorRetry = 2;

        public static AmazonS3Config Apply(AmazonS3Config config)
        {
            config.MaxErrorRetry = MaxErrorRetry;
            return config;
        }
    }
}
