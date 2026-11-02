using Estranged.Lfs.Api;
using Estranged.Lfs.Api.Controllers;
using Estranged.Lfs.Api.Entities;
using Estranged.Lfs.Data;
using Estranged.Lfs.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
namespace Estranged.Lfs.Registry.Tests;
public sealed class BatchTests
{
    const string Authorization = "Basic c2VjcmV0LXVzZXI6c2VjcmV0LXBhc3N3b3Jk";

    sealed class RecordingLogger<T> : ILogger<T>
    {
        public readonly List<(LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object>> State, Exception Exception)> Entries = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), state as IReadOnlyList<KeyValuePair<string, object>> ?? Array.Empty<KeyValuePair<string, object>>(), exception));
    }

    static (ObjectsController Controller, DefaultHttpContext Http, RecordingLogger<ObjectsController> Logger) Create(IObjectManager manager, LfsBatchOptions options = null)
    {
        var logger = new RecordingLogger<ObjectsController>();
        var http = new DefaultHttpContext();
        http.Request.Headers.Authorization = Authorization;
        var controller = new ObjectsController(manager, logger, options ?? new LfsBatchOptions()) { ControllerContext = new ControllerContext { HttpContext = http } };
        return (controller, http, logger);
    }

    static BatchRequest Request(LfsOperation operation, int count = 3) => new()
    {
        Operation = operation,
        Objects = Enumerable.Range(0, count).Select(i => new RequestObject { Oid = $"oid-{i}", Size = i }).ToList()
    };

    static int StatusOf(ActionResult<BatchResponse> result) => Assert.IsAssignableFrom<IStatusCodeActionResult>(result.Result).StatusCode ?? 200;

    [Theory]
    [InlineData(LfsOperation.Download)]
    [InlineData(LfsOperation.Upload)]
    public async Task RequestBudgetExpiryAsksTheClientToRetry(LfsOperation operation)
    {
        var manager = new Mock<IObjectManager>();
        Func<IList<RequestObject>, CancellationToken, Task<IEnumerable<ResponseObject>>> hang = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Array.Empty<ResponseObject>();
        };
        manager.Setup(x => x.DownloadObjects(It.IsAny<IList<RequestObject>>(), It.IsAny<CancellationToken>())).Returns(hang);
        manager.Setup(x => x.UploadObjects(It.IsAny<IList<RequestObject>>(), It.IsAny<CancellationToken>())).Returns(hang);
        var (controller, http, logger) = Create(manager.Object, new LfsBatchOptions { RequestBudget = TimeSpan.FromMilliseconds(50), RetryAfter = TimeSpan.FromSeconds(7) });

        var result = await controller.BatchAsync("hiddenswitch", "datasets-pixel-art", Request(operation)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(429, StatusOf(result));
        Assert.Equal("7", http.Response.Headers.RetryAfter.ToString());
        Assert.Contains(logger.Entries, x => x.Level == LogLevel.Warning && x.Exception is OperationCanceledException);
    }

    [Fact]
    public async Task TransientUpstreamFailureAsksTheClientToRetryAndLogsTheBatchWithoutCredentials()
    {
        var failure = new TransientUpstreamException("object storage", new HttpRequestException("Connection reset"));
        var manager = new Mock<IObjectManager>();
        manager.Setup(x => x.DownloadObjects(It.IsAny<IList<RequestObject>>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        var (controller, http, logger) = Create(manager.Object);

        var result = await controller.BatchAsync("hiddenswitch", "datasets-pixel-art", Request(LfsOperation.Download, 100));

        Assert.Equal(429, StatusOf(result));
        Assert.Equal("10", http.Response.Headers.RetryAfter.ToString());
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Same(failure, entry.Exception);
        var state = entry.State.ToDictionary(x => x.Key, x => x.Value);
        Assert.Equal("hiddenswitch", state["Org"]);
        Assert.Equal("datasets-pixel-art", state["Repo"]);
        Assert.Equal("download", state["Operation"]);
        Assert.Equal(100, state["ObjectCount"]);
        Assert.DoesNotContain(logger.Entries, x => x.Message.Contains(Authorization) || x.Message.Contains("c2VjcmV0") || x.Message.Contains("X-Amz-Signature"));
    }

    [Fact]
    public async Task UnexpectedFailuresAreLoggedWithTheBatchAndStillFail()
    {
        var failure = new NullReferenceException("bug");
        var manager = new Mock<IObjectManager>();
        manager.Setup(x => x.UploadObjects(It.IsAny<IList<RequestObject>>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        var (controller, _, logger) = Create(manager.Object);

        Assert.Same(failure, await Assert.ThrowsAsync<NullReferenceException>(() => controller.BatchAsync("AppMana", "game", Request(LfsOperation.Upload, 2))));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(failure, entry.Exception);
        var state = entry.State.ToDictionary(x => x.Key, x => x.Value);
        Assert.Equal("AppMana", state["Org"]);
        Assert.Equal("game", state["Repo"]);
        Assert.Equal("upload", state["Operation"]);
        Assert.Equal(2, state["ObjectCount"]);
    }

    [Fact]
    public async Task StorageAccessDeniedIsForbidden()
    {
        var manager = new Mock<IObjectManager>();
        manager.Setup(x => x.DownloadObjects(It.IsAny<IList<RequestObject>>(), It.IsAny<CancellationToken>())).ThrowsAsync(new UnauthorizedAccessException("Repository storage exchange failed (403)."));
        var (controller, http, _) = Create(manager.Object);

        var result = await controller.BatchAsync("r", "6dd8f38e-dab5-40b0-b6b7-91c412a5186b", Request(LfsOperation.Download));

        Assert.Equal(403, StatusOf(result));
        Assert.False(http.Response.Headers.ContainsKey("Retry-After"));
    }

    [Fact]
    public async Task UnsupportedOrMissingRequestsAreUnprocessable()
    {
        var manager = new Mock<IObjectManager>(MockBehavior.Strict);
        var (controller, _, _) = Create(manager.Object);

        Assert.Equal(422, StatusOf(await controller.BatchAsync("AppMana", "game", Request(LfsOperation.Verify))));
        Assert.Equal(422, StatusOf(await controller.BatchAsync("AppMana", "game", null)));
        Assert.Equal(422, StatusOf(await controller.BatchAsync("AppMana", "game", new BatchRequest { Operation = LfsOperation.Download })));
    }

    [Fact]
    public async Task SuccessfulBatchesKeepTheirResponseAndDefaultToBasicTransfer()
    {
        var objects = new[] { new ResponseObject { Oid = "oid-0", Size = 1 } };
        var manager = new Mock<IObjectManager>();
        manager.Setup(x => x.DownloadObjects(It.IsAny<IList<RequestObject>>(), It.IsAny<CancellationToken>())).ReturnsAsync(objects);
        var (controller, _, _) = Create(manager.Object);
        var request = Request(LfsOperation.Download);
        request.Transfers = null;

        var result = await controller.BatchAsync("AppMana", "game", request);

        Assert.Equal("basic", result.Value.Transfer);
        Assert.Same(objects, result.Value.Objects);
    }
}
