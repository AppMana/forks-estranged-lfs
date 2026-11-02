using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Estranged.Lfs.Adapter.S3
{
    public interface ISharedObjectPopulator
    {
        /// <summary>
        /// Requests that <paramref name="sharedKey"/> be populated from
        /// <paramref name="sourceKey"/>. Returns false when the work was dropped.
        /// </summary>
        bool TryEnqueue(string bucket, string sourceKey, string sharedKey);
    }

    /// <summary>
    /// Populates canonical content-addressed objects off the request path with
    /// a bounded queue and a fixed set of workers. The canonical objects are a
    /// best-effort deduplication cache, so work is dropped when the queue is
    /// full and failures are logged, never surfaced to requests.
    /// </summary>
    public sealed class SharedObjectPopulator : ISharedObjectPopulator, IAsyncDisposable, IDisposable
    {
        private sealed record Job(string Bucket, string SourceKey, string SharedKey);

        private readonly IAmazonS3 client;
        private readonly ILogger<SharedObjectPopulator> logger;
        private readonly TimeSpan copyTimeout;
        private readonly Channel<Job> queue;
        private readonly ConcurrentDictionary<string, byte> pending = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource stopping = new();
        private readonly Task[] workers;
        private int disposed;

        public SharedObjectPopulator(IAmazonS3 client, ILogger<SharedObjectPopulator> logger = null, int capacity = 1024, int workers = 2, TimeSpan? copyTimeout = null)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (workers < 1) throw new ArgumentOutOfRangeException(nameof(workers));
            this.client = client;
            this.logger = logger ?? NullLogger<SharedObjectPopulator>.Instance;
            this.copyTimeout = copyTimeout ?? TimeSpan.FromMinutes(10);
            queue = Channel.CreateBounded<Job>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = workers == 1,
                SingleWriter = false
            });
            this.workers = Enumerable.Range(0, workers).Select(_ => Task.Run(WorkAsync)).ToArray();
        }

        public bool TryEnqueue(string bucket, string sourceKey, string sharedKey)
        {
            string identity = bucket + "/" + sharedKey;
            if (!pending.TryAdd(identity, 0))
                return true;
            if (queue.Writer.TryWrite(new Job(bucket, sourceKey, sharedKey)))
                return true;
            pending.TryRemove(identity, out _);
            return false;
        }

        private async Task WorkAsync()
        {
            try
            {
                while (await queue.Reader.WaitToReadAsync(stopping.Token).ConfigureAwait(false))
                {
                    while (queue.Reader.TryRead(out var job))
                    {
                        try
                        {
                            await PopulateAsync(job).ConfigureAwait(false);
                        }
                        finally
                        {
                            pending.TryRemove(job.Bucket + "/" + job.SharedKey, out _);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
            }
        }

        private async Task PopulateAsync(Job job)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
            timeout.CancelAfter(copyTimeout);
            try
            {
                try
                {
                    await client.GetObjectMetadataAsync(job.Bucket, job.SharedKey, timeout.Token).ConfigureAwait(false);
                    return;
                }
                catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
                {
                }

                await client.CopyObjectAsync(new CopyObjectRequest
                {
                    SourceBucket = job.Bucket,
                    SourceKey = job.SourceKey,
                    DestinationBucket = job.Bucket,
                    DestinationKey = job.SharedKey
                }, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not populate canonical object {SharedKey} in {Bucket}", job.SharedKey, job.Bucket);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            queue.Writer.TryComplete();
            stopping.CancelAfter(TimeSpan.FromSeconds(5));
            await Task.WhenAll(workers).ConfigureAwait(false);
            stopping.Dispose();
        }

        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
