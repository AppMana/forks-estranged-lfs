using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Estranged.Lfs.Adapter.S3;
using Moq;
using System;
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

            mockClient.Setup(x => x.GetObjectMetadataAsync("my-bucket", "prefix/d53de494a038b6a8ede0aea08c38bde00244b155924bf4c463d1de208faecee8", CancellationToken.None))
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

            mockClient.Setup(x => x.GetObjectMetadataAsync("my-bucket", "prefix/wibble", CancellationToken.None))
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

            mockClient.Setup(x => x.GetObjectMetadataAsync("my-bucket", $"AppMana/appmana/{oid}", CancellationToken.None))
                      .ThrowsAsync(new AmazonS3Exception("Object not found", ErrorType.Sender, "NotFound", "requestId", HttpStatusCode.NotFound));

            var adapter = new S3BlobAdapter(mockClient.Object, new S3BlobAdapterConfig
            {
                Bucket = "my-bucket",
                KeyPrefix = "AppMana/appmana/"
            });

            var signedBlob = await adapter.UriForDownload(oid, CancellationToken.None);

            Assert.Equal(404, signedBlob.ErrorCode);
            mockClient.Verify(x => x.GetObjectMetadataAsync("my-bucket", oid, CancellationToken.None), Times.Never);
            mockClient.Verify(x => x.GetObjectMetadataAsync("my-bucket", $"AppMana/Art-Workspace/{oid}", CancellationToken.None), Times.Never);
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

            mockClient.Setup(x => x.GetObjectMetadataAsync("my-bucket", $"AppMana/repo-b/{oid}", CancellationToken.None))
                      .ThrowsAsync(new AmazonS3Exception("Not found", ErrorType.Sender, "NotFound", "requestId", HttpStatusCode.NotFound));
            mockClient.Setup(x => x.GetObjectMetadataAsync("my-bucket", $"objects/{oid}", CancellationToken.None))
                      .ReturnsAsync(new GetObjectMetadataResponse { ContentLength = 4096 });
            mockClient.Setup(x => x.CopyObjectAsync(It.Is<CopyObjectRequest>(r =>
                          r.SourceBucket == "my-bucket" &&
                          r.SourceKey == $"objects/{oid}" &&
                          r.DestinationBucket == "my-bucket" &&
                          r.DestinationKey == $"AppMana/repo-b/{oid}"), CancellationToken.None))
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

        [Fact]
        public async Task DownloadBlobPopulatesCanonicalSharedPrefixWhenMissing()
        {
            var mockClient = mockRepository.Create<IAmazonS3>();
            const string oid = "d53de494a038b6a8ede0aea08c38bde00244b155924bf4c463d1de208faecee8";

            mockClient.Setup(x => x.GetObjectMetadataAsync("my-bucket", $"AppMana/repo-a/{oid}", CancellationToken.None))
                      .ReturnsAsync(new GetObjectMetadataResponse { ContentLength = 8192 });
            mockClient.Setup(x => x.GetObjectMetadataAsync("my-bucket", $"objects/{oid}", CancellationToken.None))
                      .ThrowsAsync(new AmazonS3Exception("Not found", ErrorType.Sender, "NotFound", "requestId", HttpStatusCode.NotFound));
            mockClient.Setup(x => x.CopyObjectAsync(It.Is<CopyObjectRequest>(r =>
                          r.SourceBucket == "my-bucket" &&
                          r.SourceKey == $"AppMana/repo-a/{oid}" &&
                          r.DestinationBucket == "my-bucket" &&
                          r.DestinationKey == $"objects/{oid}"), CancellationToken.None))
                      .ReturnsAsync(new CopyObjectResponse());
            mockClient.Setup(x => x.Config).Returns(new AmazonS3Config { ServiceURL = "https://s3.example" });
            mockClient.Setup(x => x.GetPreSignedURL(It.IsAny<GetPreSignedUrlRequest>()))
                      .Returns($"https://s3.example/my-bucket/AppMana/repo-a/{oid}");

            var adapter = new S3BlobAdapter(mockClient.Object, new S3BlobAdapterConfig
            {
                Bucket = "my-bucket",
                KeyPrefix = "AppMana/repo-a/",
                SharedKeyPrefix = "objects/",
                DeduplicateUploads = true
            });

            var signedBlob = await adapter.UriForDownload(oid, CancellationToken.None);

            Assert.Null(signedBlob.ErrorCode);
            Assert.Equal(8192, signedBlob.Size);
            Assert.Equal($"https://s3.example/my-bucket/AppMana/repo-a/{oid}", signedBlob.Uri.ToString());
        }
    }
}
