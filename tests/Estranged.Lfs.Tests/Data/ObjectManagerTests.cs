using Estranged.Lfs.Data;
using Estranged.Lfs.Data.Entities;
using Moq;
using System.Collections.Generic;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Estranged.Lfs.Tests.Data
{
    public class ObjectManagerTests
    {
        [Fact]
        public async Task DownloadObjectsReturnsBlobAdapterErrorForMissingObject()
        {
            var blobAdapter = new Mock<IBlobAdapter>(MockBehavior.Strict);
            blobAdapter
                .Setup(x => x.UriForDownload("missing-oid", CancellationToken.None))
                .ReturnsAsync(new SignedBlob
                {
                    ErrorCode = 404,
                    ErrorMessage = "Object not found"
                });

            var manager = new ObjectManager(blobAdapter.Object);

            var response = (await manager.DownloadObjects(new List<RequestObject>
            {
                new RequestObject { Oid = "missing-oid", Size = 123 }
            }, CancellationToken.None)).Single();

            Assert.Equal("missing-oid", response.Oid);
            Assert.Equal(123, response.Size);
            Assert.Null(response.Authenticated);
            Assert.NotNull(response.Error);
            Assert.Equal(404, response.Error.Code);
            Assert.Equal("Object not found", response.Error.Message);
            Assert.Null(response.Actions.Download);
            blobAdapter.VerifyAll();
        }

        [Fact]
        public async Task DownloadObjectsOverlapsLookupsWithinTheConcurrencyLimit()
        {
            var active = 0;
            var peak = 0;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blobAdapter = new Mock<IBlobAdapter>(MockBehavior.Strict);
            blobAdapter
                .Setup(x => x.UriForDownload(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(async (string oid, CancellationToken _) =>
                {
                    var now = Interlocked.Increment(ref active);
                    int seen;
                    while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
                    if (now == ObjectManager.MaxConcurrentDownloadLookups) release.TrySetResult();
                    await release.Task;
                    Interlocked.Decrement(ref active);
                    return new SignedBlob { Uri = new Uri($"https://storage.example/{oid}"), Size = 1, Expiry = TimeSpan.FromMinutes(5) };
                });

            var objects = Enumerable.Range(0, 100).Select(i => new RequestObject { Oid = $"oid-{i}", Size = 1 }).ToList();
            var response = (await new ObjectManager(blobAdapter.Object).DownloadObjects(objects, CancellationToken.None)).ToList();

            Assert.Equal(ObjectManager.MaxConcurrentDownloadLookups, peak);
            Assert.Equal(100, response.Count);
            Assert.All(response, x => Assert.NotNull(x.Actions.Download));
        }

        [Fact]
        public async Task DownloadObjectsKeepsRequestOrderWhenLookupsFinishOutOfOrder()
        {
            var blobAdapter = new Mock<IBlobAdapter>(MockBehavior.Strict);
            blobAdapter
                .Setup(x => x.UriForDownload(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(async (string oid, CancellationToken _) =>
                {
                    await Task.Delay(oid == "first" ? 50 : 0);
                    return oid == "missing"
                        ? new SignedBlob { ErrorCode = 404, ErrorMessage = "Object not found" }
                        : new SignedBlob { Uri = new Uri($"https://storage.example/{oid}"), Size = oid.Length, Expiry = TimeSpan.FromMinutes(5) };
                });

            var response = (await new ObjectManager(blobAdapter.Object).DownloadObjects(new List<RequestObject>
            {
                new RequestObject { Oid = "first", Size = 9 },
                new RequestObject { Oid = "missing", Size = 9 },
                new RequestObject { Oid = "last", Size = 9 },
            }, CancellationToken.None)).ToList();

            Assert.Equal(new[] { "first", "missing", "last" }, response.Select(x => x.Oid));
            Assert.Equal(new Uri("https://storage.example/first"), response[0].Actions.Download.Href);
            Assert.Equal(404, response[1].Error.Code);
            Assert.Equal(4, response[2].Size);
        }

        [Fact]
        public async Task FallbackBlobAdapterUsesFallbackOnlyForMissingDownloads()
        {
            var primary = new Mock<IBlobAdapter>(MockBehavior.Strict);
            var fallback = new Mock<IBlobAdapter>(MockBehavior.Strict);

            primary.Setup(x => x.UriForDownload("missing-oid", CancellationToken.None))
                   .ReturnsAsync(new SignedBlob { ErrorCode = 404, ErrorMessage = "Object not found" });
            fallback.Setup(x => x.UriForDownload("missing-oid", CancellationToken.None))
                    .ReturnsAsync(new SignedBlob { Uri = new Uri("https://fallback.example/missing-oid"), Size = 123 });
            primary.Setup(x => x.UriForDownload("forbidden-oid", CancellationToken.None))
                   .ReturnsAsync(new SignedBlob { ErrorCode = 403, ErrorMessage = "Forbidden" });
            primary.Setup(x => x.UriForUpload("upload-oid", 456, CancellationToken.None))
                   .ReturnsAsync(new SignedBlob { Uri = new Uri("https://primary.example/upload-oid") });

            var adapter = new FallbackBlobAdapter(primary.Object, fallback.Object);

            var missing = await adapter.UriForDownload("missing-oid", CancellationToken.None);
            var forbidden = await adapter.UriForDownload("forbidden-oid", CancellationToken.None);
            var upload = await adapter.UriForUpload("upload-oid", 456, CancellationToken.None);

            Assert.Equal(new Uri("https://fallback.example/missing-oid"), missing.Uri);
            Assert.Equal(123, missing.Size);
            Assert.Equal(403, forbidden.ErrorCode);
            Assert.Equal(new Uri("https://primary.example/upload-oid"), upload.Uri);
            primary.VerifyAll();
            fallback.VerifyAll();
        }
    }
}
