using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Estranged.Lfs.Adapter.S3;
using Estranged.Lfs.Data;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Estranged.Lfs.Tests.Adapter.S3
{
    public class S3BlobAdapterTests : IDisposable
    {
        private readonly MockRepository mockRepository = new MockRepository(MockBehavior.Strict);

        public void Dispose() => mockRepository.VerifyAll();

        [Fact]
        public async Task TestDownloadBlobFound()
        {
            var mockClient = mockRepository.Create<IAmazonS3>();

            mockClient.Setup(x => x.GetObjectMetadataAsync("my-bucket", "prefix/d53de494a038b6a8ede0aea08c38bde00244b155924bf4c463d1de208faecee8", It.IsAny<CancellationToken>()))
                      .ReturnsAsync(new GetObjectMetadataResponse{ContentLength = 19961});

            mockClient.Setup(x => x.Config).Returns(new AmazonS3Config { ServiceURL = "https://s3.example" });
            mockClient.Setup(x => x.GetPreSignedURL(It.IsAny<GetPreSignedUrlRequest>()))
                      .Callback<GetPreSignedUrlRequest>(request =>
                      {
                          Assert.Equal("prefix/d53de494a038b6a8ede0aea08c38bde00244b155924bf4c463d1de208faecee8", request.Key);
                          Assert.Equal("my-bucket", request.BucketName);
                          Assert.Equal(HttpVerb.GET, request.Verb);
                          Assert.Equal(Protocol.HTTPS, request.Protocol);
                      })
                      .Returns("https://www.example.com/");

            var adapter = new S3BlobAdapter(mockClient.Object, new S3BlobAdapterConfig { Bucket = "my-bucket", KeyPrefix = "prefix/" });

            var signedBlob = await adapter.UriForDownload("d53de494a038b6a8ede0aea08c38bde00244b155924bf4c463d1de208faecee8", CancellationToken.None);

            Assert.Null(signedBlob.ErrorCode);
            Assert.Null(signedBlob.ErrorMessage);
            Assert.Equal(19961, signedBlob.Size);
            Assert.Equal("https://www.example.com/", signedBlob.Uri.ToString());
        }

        [Fact]
        public async Task TestDownloadBlobNotFound()
        {
            var mockClient = mockRepository.Create<IAmazonS3>();

            mockClient.Setup(x => x.GetObjectMetadataAsync("my-bucket", "prefix/wibble", It.IsAny<CancellationToken>()))
                      .ThrowsAsync(new AmazonS3Exception("Error making request with Error Code NotFound and Http Status Code NotFound. No further error information was returned by the service.", ErrorType.Sender, "NotFound", "requestId", HttpStatusCode.NotFound));

            var adapter = new S3BlobAdapter(mockClient.Object, new S3BlobAdapterConfig { Bucket = "my-bucket", KeyPrefix = "prefix/" });

            var signedBlob = await adapter.UriForDownload("wibble", CancellationToken.None);

            Assert.Equal(404, signedBlob.ErrorCode);
            Assert.Equal("Error making request with Error Code NotFound and Http Status Code NotFound. No further error information was returned by the service.", signedBlob.ErrorMessage);
        }

        [Fact]
        public async Task DownloadDoesNotReturnSameOidFromAnotherRepositoryPrefix()
        {
            var mockClient = mockRepository.Create<IAmazonS3>();
            const string oid = "d53de494a038b6a8ede0aea08c38bde00244b155924bf4c463d1de208faecee8";

            mockClient.Setup(x => x.GetObjectMetadataAsync("my-bucket", $"AppMana/appmana/{oid}", It.IsAny<CancellationToken>()))
                      .ThrowsAsync(new AmazonS3Exception("Object not found", ErrorType.Sender, "NotFound", "requestId", HttpStatusCode.NotFound));

            var adapter = new S3BlobAdapter(mockClient.Object, new S3BlobAdapterConfig
            {
                Bucket = "my-bucket",
                KeyPrefix = "AppMana/appmana/"
            });

            var signedBlob = await adapter.UriForDownload(oid, CancellationToken.None);

            Assert.Equal(404, signedBlob.ErrorCode);
            mockClient.Verify(x => x.GetObjectMetadataAsync("my-bucket", oid, It.IsAny<CancellationToken>()), Times.Never);
            mockClient.Verify(x => x.GetObjectMetadataAsync("my-bucket", $"AppMana/Art-Workspace/{oid}", It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task TestUploadBlob()
        {
            var mockClient = mockRepository.Create<IAmazonS3>();

            mockClient.Setup(x => x.Config).Returns(new AmazonS3Config { ServiceURL = "https://s3.example" });
            mockClient.Setup(x => x.GetPreSignedURL(It.IsAny<GetPreSignedUrlRequest>()))
                      .Callback<GetPreSignedUrlRequest>(request =>
                      {
                          Assert.Equal("prefix/wibble", request.Key);
                          Assert.Equal("my-bucket", request.BucketName);
                          Assert.Equal(HttpVerb.PUT, request.Verb);
                          Assert.Equal(Protocol.HTTPS, request.Protocol);
                      })
                      .Returns("https://www.example.com/");

            var adapter = new S3BlobAdapter(mockClient.Object, new S3BlobAdapterConfig { Bucket = "my-bucket", KeyPrefix = "prefix/" });

            var signedBlob = await adapter.UriForUpload("wibble", 10, CancellationToken.None);

            Assert.Null(signedBlob.ErrorCode);
            Assert.Null(signedBlob.ErrorMessage);
            Assert.Equal("https://www.example.com/", signedBlob.Uri.ToString());
        }
        [Fact]
        public void PresignedUrlsFollowThePlainHttpEndpointScheme()
        {
            var mockClient = mockRepository.Create<IAmazonS3>();
            mockClient.Setup(x => x.Config).Returns(new AmazonS3Config { ServiceURL = "http://minio.internal:9000" });
            mockClient.Setup(x => x.GetPreSignedURL(It.IsAny<GetPreSignedUrlRequest>()))
                      .Callback<GetPreSignedUrlRequest>(request => Assert.Equal(Protocol.HTTP, request.Protocol))
                      .Returns("http://minio.internal:9000/my-bucket/prefix/oid");

            var adapter = new S3BlobAdapter(mockClient.Object, new S3BlobAdapterConfig { Bucket = "my-bucket", KeyPrefix = "prefix/" });

            Assert.Equal("http://minio.internal:9000/my-bucket/prefix/oid", adapter.MakePreSignedUrl("oid", HttpVerb.PUT, "application/octet-stream").ToString());
            Assert.Equal(Protocol.HTTPS, S3BlobAdapter.ProtocolFor(null));
            Assert.Equal(Protocol.HTTPS, S3BlobAdapter.ProtocolFor("https://s3.amazonaws.com"));
        }

        [Fact]
        public async Task UploadBlobDeduplicatesFromCanonicalSharedPrefixViaCopyObject()
        {
            var mockClient = mockRepository.Create<IAmazonS3>();
            const string oid = "d53de494a038b6a8ede0aea08c38bde00244b155924bf4c463d1de208faecee8";

            mockClient.Setup(x => x.GetObjectMetadataAsync("my-bucket", $"AppMana/repo-b/{oid}", It.IsAny<CancellationToken>()))
                      .ThrowsAsync(new AmazonS3Exception("Not found", ErrorType.Sender, "NotFound", "requestId", HttpStatusCode.NotFound));
            mockClient.Setup(x => x.GetObjectMetadataAsync("my-bucket", $"objects/{oid}", It.IsAny<CancellationToken>()))
                      .ReturnsAsync(new GetObjectMetadataResponse { ContentLength = 4096 });
            mockClient.Setup(x => x.CopyObjectAsync(It.Is<CopyObjectRequest>(r =>
                          r.SourceBucket == "my-bucket" &&
                          r.SourceKey == $"objects/{oid}" &&
                          r.DestinationBucket == "my-bucket" &&
                          r.DestinationKey == $"AppMana/repo-b/{oid}"), It.IsAny<CancellationToken>()))
                      .ReturnsAsync(new CopyObjectResponse());

            var adapter = new S3BlobAdapter(mockClient.Object, new S3BlobAdapterConfig
            {
                Bucket = "my-bucket",
                KeyPrefix = "AppMana/repo-b/",
                SharedKeyPrefix = "objects/",
                DeduplicateUploads = true
            });

            var signedBlob = await adapter.UriForUpload(oid, 4096, CancellationToken.None);

            Assert.True(signedBlob.AlreadyExists);
            Assert.Null(signedBlob.Uri);
            Assert.Equal(4096, signedBlob.Size);
        }

        private const string Oid = "d53de494a038b6a8ede0aea08c38bde00244b155924bf4c463d1de208faecee8";
        private static readonly TransientRetryPolicy Fast = new() { MaxAttempts = 3, InitialDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero };

        private sealed class RecordingPopulator : ISharedObjectPopulator
        {
            public readonly List<(string Bucket, string SourceKey, string SharedKey)> Queued = new();
            public bool TryEnqueue(string bucket, string sourceKey, string sharedKey)
            {
                Queued.Add((bucket, sourceKey, sharedKey));
                return true;
            }
        }

        private Mock<IAmazonS3> PresigningClient()
        {
            var mockClient = mockRepository.Create<IAmazonS3>();
            mockClient.Setup(x => x.Config).Returns(new AmazonS3Config { ServiceURL = "https://s3.example" });
            mockClient.Setup(x => x.GetPreSignedURL(It.IsAny<GetPreSignedUrlRequest>()))
                      .Returns((GetPreSignedUrlRequest request) => $"https://s3.example/{request.BucketName}/{request.Key}?verb={request.Verb}");
            return mockClient;
        }

        private static S3BlobAdapterConfig RepoA(TimeSpan? deduplicationTimeout = null) => new()
        {
            Bucket = "my-bucket",
            KeyPrefix = "AppMana/repo-a/",
            SharedKeyPrefix = "objects/",
            DeduplicateUploads = true,
            DeduplicationTimeout = deduplicationTimeout ?? TimeSpan.FromSeconds(2)
        };

        [Fact]
        public async Task DownloadQueuesCanonicalPopulationInsteadOfCopyingOnTheRequestPath()
        {
            var mockClient = PresigningClient();
            mockClient.Setup(x => x.GetObjectMetadataAsync("my-bucket", $"AppMana/repo-a/{Oid}", It.IsAny<CancellationToken>()))
                      .ReturnsAsync(new GetObjectMetadataResponse { ContentLength = 8192 });
            var populator = new RecordingPopulator();

            var adapter = new S3BlobAdapter(mockClient.Object, RepoA(), populator: populator, retryPolicy: Fast);
            var signedBlob = await adapter.UriForDownload(Oid, CancellationToken.None);

            Assert.Null(signedBlob.ErrorCode);
            Assert.Equal(8192, signedBlob.Size);
            Assert.Equal($"https://s3.example/my-bucket/AppMana/repo-a/{Oid}?verb=GET", signedBlob.Uri.ToString());
            Assert.Equal(("my-bucket", $"AppMana/repo-a/{Oid}", $"objects/{Oid}"), Assert.Single(populator.Queued));
            mockClient.Verify(x => x.GetObjectMetadataAsync("my-bucket", $"objects/{Oid}", It.IsAny<CancellationToken>()), Times.Never);
            mockClient.Verify(x => x.CopyObjectAsync(It.IsAny<CopyObjectRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task DownloadRetriesTransientStorageFailures()
        {
            var mockClient = PresigningClient();
            mockClient.SetupSequence(x => x.GetObjectMetadataAsync("my-bucket", $"AppMana/repo-a/{Oid}", It.IsAny<CancellationToken>()))
                      .ThrowsAsync(new AmazonS3Exception("Slow down", ErrorType.Receiver, "SlowDown", "requestId", HttpStatusCode.ServiceUnavailable))
                      .ThrowsAsync(new HttpRequestException("Connection reset"))
                      .ReturnsAsync(new GetObjectMetadataResponse { ContentLength = 10 });

            var adapter = new S3BlobAdapter(mockClient.Object, RepoA(), retryPolicy: Fast);
            var signedBlob = await adapter.UriForDownload(Oid, CancellationToken.None);

            Assert.Null(signedBlob.ErrorCode);
            Assert.Equal(10, signedBlob.Size);
        }

        public static IEnumerable<object[]> TransientStorageFailures() => new[]
        {
            new object[] { new AmazonS3Exception("Internal error", ErrorType.Receiver, "InternalError", "requestId", HttpStatusCode.InternalServerError) },
            new object[] { new AmazonS3Exception("Slow down", ErrorType.Receiver, "SlowDown", "requestId", HttpStatusCode.ServiceUnavailable) },
            new object[] { new AmazonServiceException("Bad gateway", ErrorType.Receiver, "BadGateway", "requestId", HttpStatusCode.BadGateway) },
            new object[] { new HttpRequestException("Connection refused") },
            new object[] { new IOException("Unexpected end of stream") },
            new object[] { new TaskCanceledException("HttpClient.Timeout elapsed") },
        };

        [Theory]
        [MemberData(nameof(TransientStorageFailures))]
        public async Task ExhaustedTransientStorageFailuresFailTheBatchRetryablyNotTheObject(Exception failure)
        {
            var mockClient = mockRepository.Create<IAmazonS3>();
            mockClient.Setup(x => x.GetObjectMetadataAsync("my-bucket", $"AppMana/repo-a/{Oid}", It.IsAny<CancellationToken>()))
                      .ThrowsAsync(failure);

            var adapter = new S3BlobAdapter(mockClient.Object, RepoA(), retryPolicy: Fast);
            var thrown = await Assert.ThrowsAsync<TransientUpstreamException>(() => adapter.UriForDownload(Oid, CancellationToken.None));

            Assert.Same(failure, thrown.InnerException);
            mockClient.Verify(x => x.GetObjectMetadataAsync("my-bucket", $"AppMana/repo-a/{Oid}", It.IsAny<CancellationToken>()), Times.Exactly(3));
        }

        [Fact]
        public async Task DefinitiveStorageAnswersStayObjectErrorsWithoutRetry()
        {
            var mockClient = mockRepository.Create<IAmazonS3>();
            mockClient.Setup(x => x.GetObjectMetadataAsync("my-bucket", $"AppMana/repo-a/{Oid}", It.IsAny<CancellationToken>()))
                      .ThrowsAsync(new AmazonS3Exception("Access denied", ErrorType.Sender, "AccessDenied", "requestId", HttpStatusCode.Forbidden));

            var adapter = new S3BlobAdapter(mockClient.Object, RepoA(), retryPolicy: Fast);
            var signedBlob = await adapter.UriForDownload(Oid, CancellationToken.None);

            Assert.Equal(403, signedBlob.ErrorCode);
            mockClient.Verify(x => x.GetObjectMetadataAsync("my-bucket", $"AppMana/repo-a/{Oid}", It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task UploadOfAnObjectAlreadyInTheRepositoryQueuesCanonicalPopulation()
        {
            var mockClient = mockRepository.Create<IAmazonS3>();
            mockClient.Setup(x => x.GetObjectMetadataAsync("my-bucket", $"AppMana/repo-a/{Oid}", It.IsAny<CancellationToken>()))
                      .ReturnsAsync(new GetObjectMetadataResponse { ContentLength = 77 });
            var populator = new RecordingPopulator();

            var adapter = new S3BlobAdapter(mockClient.Object, RepoA(), populator: populator, retryPolicy: Fast);
            var signedBlob = await adapter.UriForUpload(Oid, 77, CancellationToken.None);

            Assert.True(signedBlob.AlreadyExists);
            Assert.Equal(("my-bucket", $"AppMana/repo-a/{Oid}", $"objects/{Oid}"), Assert.Single(populator.Queued));
            mockClient.Verify(x => x.CopyObjectAsync(It.IsAny<CopyObjectRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task UploadFallsBackToAPresignedUploadWhenTheDeduplicationCopyFails()
        {
            var mockClient = PresigningClient();
            mockClient.Setup(x => x.GetObjectMetadataAsync("my-bucket", $"AppMana/repo-a/{Oid}", It.IsAny<CancellationToken>()))
                      .ThrowsAsync(new AmazonS3Exception("Not found", ErrorType.Sender, "NotFound", "requestId", HttpStatusCode.NotFound));
            mockClient.Setup(x => x.GetObjectMetadataAsync("my-bucket", $"objects/{Oid}", It.IsAny<CancellationToken>()))
                      .ReturnsAsync(new GetObjectMetadataResponse { ContentLength = 5 });
            mockClient.Setup(x => x.CopyObjectAsync(It.IsAny<CopyObjectRequest>(), It.IsAny<CancellationToken>()))
                      .ThrowsAsync(new HttpRequestException("Connection reset"));

            var adapter = new S3BlobAdapter(mockClient.Object, RepoA(), retryPolicy: Fast);
            var signedBlob = await adapter.UriForUpload(Oid, 5, CancellationToken.None);

            Assert.False(signedBlob.AlreadyExists);
            Assert.Equal($"https://s3.example/my-bucket/AppMana/repo-a/{Oid}?verb=PUT", signedBlob.Uri.ToString());
        }

        [Fact]
        public async Task UploadFallsBackToAPresignedUploadWhenDeduplicationIsSlow()
        {
            var mockClient = PresigningClient();
            mockClient.Setup(x => x.GetObjectMetadataAsync("my-bucket", $"AppMana/repo-a/{Oid}", It.IsAny<CancellationToken>()))
                      .ThrowsAsync(new AmazonS3Exception("Not found", ErrorType.Sender, "NotFound", "requestId", HttpStatusCode.NotFound));
            mockClient.Setup(x => x.GetObjectMetadataAsync("my-bucket", $"objects/{Oid}", It.IsAny<CancellationToken>()))
                      .ReturnsAsync(new GetObjectMetadataResponse { ContentLength = 5 });
            mockClient.Setup(x => x.CopyObjectAsync(It.IsAny<CopyObjectRequest>(), It.IsAny<CancellationToken>()))
                      .Returns(async (CopyObjectRequest _, CancellationToken token) =>
                      {
                          await Task.Delay(Timeout.Infinite, token);
                          return new CopyObjectResponse();
                      });

            var adapter = new S3BlobAdapter(mockClient.Object, RepoA(TimeSpan.FromMilliseconds(50)), retryPolicy: Fast);
            var signedBlob = await adapter.UriForUpload(Oid, 5, CancellationToken.None);

            Assert.False(signedBlob.AlreadyExists);
            Assert.Equal($"https://s3.example/my-bucket/AppMana/repo-a/{Oid}?verb=PUT", signedBlob.Uri.ToString());
        }

        [Fact]
        public async Task CanonicalKeysAreDerivedOnlyFromSha256Oids()
        {
            const string traversal = "../repo-b/" + Oid;
            var mockClient = PresigningClient();
            mockClient.Setup(x => x.GetObjectMetadataAsync("my-bucket", $"AppMana/repo-a/{traversal}", It.IsAny<CancellationToken>()))
                      .ReturnsAsync(new GetObjectMetadataResponse { ContentLength = 5 });
            var populator = new RecordingPopulator();
            var config = RepoA();
            config.DeduplicateUploads = false;

            var adapter = new S3BlobAdapter(mockClient.Object, config, populator: populator, retryPolicy: Fast);
            await adapter.UriForDownload(traversal, CancellationToken.None);

            Assert.Empty(populator.Queued);
            mockClient.Verify(x => x.GetObjectMetadataAsync("my-bucket", It.Is<string>(key => key.StartsWith("objects/")), It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}
