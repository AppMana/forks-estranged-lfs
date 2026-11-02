using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Estranged.Lfs.Adapter.S3;
using Moq;
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Estranged.Lfs.Tests.Adapter.S3
{
    public class SharedObjectPopulatorTests
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

        private static AmazonS3Exception NotFound() =>
            new("Not found", ErrorType.Sender, "NotFound", "requestId", HttpStatusCode.NotFound);

        [Fact]
        public async Task CopiesMissingCanonicalObjectsInTheBackground()
        {
            var copied = new TaskCompletionSource<CopyObjectRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            var client = new Mock<IAmazonS3>(MockBehavior.Strict);
            client.Setup(x => x.GetObjectMetadataAsync("bucket", "objects/oid", It.IsAny<CancellationToken>())).ThrowsAsync(NotFound());
            client.Setup(x => x.CopyObjectAsync(It.IsAny<CopyObjectRequest>(), It.IsAny<CancellationToken>()))
                  .Callback((CopyObjectRequest request, CancellationToken _) => copied.TrySetResult(request))
                  .ReturnsAsync(new CopyObjectResponse());

            await using var populator = new SharedObjectPopulator(client.Object);
            Assert.True(populator.TryEnqueue("bucket", "AppMana/repo/oid", "objects/oid"));

            var request = await copied.Task.WaitAsync(Wait);
            Assert.Equal("bucket", request.SourceBucket);
            Assert.Equal("AppMana/repo/oid", request.SourceKey);
            Assert.Equal("bucket", request.DestinationBucket);
            Assert.Equal("objects/oid", request.DestinationKey);
        }

        [Fact]
        public async Task SkipsCanonicalObjectsThatAlreadyExistAndSurvivesFailures()
        {
            var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var client = new Mock<IAmazonS3>(MockBehavior.Strict);
            client.Setup(x => x.GetObjectMetadataAsync("bucket", "objects/present", It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new GetObjectMetadataResponse { ContentLength = 1 });
            client.Setup(x => x.GetObjectMetadataAsync("bucket", "objects/broken", It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new HttpRequestException("Connection reset"));
            client.Setup(x => x.GetObjectMetadataAsync("bucket", "objects/last", It.IsAny<CancellationToken>()))
                  .Callback(() => second.TrySetResult())
                  .ReturnsAsync(new GetObjectMetadataResponse { ContentLength = 1 });

            await using var populator = new SharedObjectPopulator(client.Object, workers: 1);
            Assert.True(populator.TryEnqueue("bucket", "repo/present", "objects/present"));
            Assert.True(populator.TryEnqueue("bucket", "repo/broken", "objects/broken"));
            Assert.True(populator.TryEnqueue("bucket", "repo/last", "objects/last"));

            await second.Task.WaitAsync(Wait);
            client.Verify(x => x.CopyObjectAsync(It.IsAny<CopyObjectRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task DropsWorkWhenFullAndCoalescesPendingKeys()
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var heads = 0;
            var client = new Mock<IAmazonS3>(MockBehavior.Strict);
            client.Setup(x => x.GetObjectMetadataAsync("bucket", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .Returns(async (string _, string key, CancellationToken token) =>
                  {
                      Interlocked.Increment(ref heads);
                      started.TrySetResult();
                      await release.Task.WaitAsync(token);
                      return new GetObjectMetadataResponse { ContentLength = 1 };
                  });

            await using var populator = new SharedObjectPopulator(client.Object, capacity: 1, workers: 1);
            Assert.True(populator.TryEnqueue("bucket", "repo/a", "objects/a"));
            await started.Task.WaitAsync(Wait);
            Assert.True(populator.TryEnqueue("bucket", "repo/b", "objects/b"));
            Assert.True(populator.TryEnqueue("bucket", "repo/b", "objects/b"));
            Assert.False(populator.TryEnqueue("bucket", "repo/c", "objects/c"));
            Assert.True(populator.TryEnqueue("bucket", "repo/a", "objects/a"));
            release.TrySetResult();
            await populator.DisposeAsync();
            Assert.Equal(2, heads);
        }

        [Fact]
        public async Task EachCopyHasItsOwnTimeout()
        {
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var client = new Mock<IAmazonS3>(MockBehavior.Strict);
            client.Setup(x => x.GetObjectMetadataAsync("bucket", "objects/slow", It.IsAny<CancellationToken>()))
                  .Returns(async (string _, string _, CancellationToken token) =>
                  {
                      try { await Task.Delay(Timeout.Infinite, token); }
                      catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
                      return new GetObjectMetadataResponse();
                  });

            await using var populator = new SharedObjectPopulator(client.Object, copyTimeout: TimeSpan.FromMilliseconds(50));
            Assert.True(populator.TryEnqueue("bucket", "repo/slow", "objects/slow"));
            await cancelled.Task.WaitAsync(Wait);
        }
    }
}
