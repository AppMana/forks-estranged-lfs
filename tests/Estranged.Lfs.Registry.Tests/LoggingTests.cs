using Estranged.Lfs.Api.Filters;
using Estranged.Lfs.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
namespace Estranged.Lfs.Registry.Tests;
public sealed class LoggingTests
{
    sealed class RecordingLogger<T> : ILogger<T>
    {
        public readonly List<(LogLevel Level, Exception Exception)> Entries = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) =>
            Entries.Add((logLevel, exception));
    }

    [Fact]
    public void HostLogsSingleLineJsonWithoutPerRequestFrameworkInformation()
    {
        using var provider = new ServiceCollection().AddLogging(LoggingSetup.Configure).BuildServiceProvider();
        var factory = provider.GetRequiredService<ILoggerFactory>();
        Assert.Equal(ConsoleFormatterNames.Json, provider.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value.FormatterName);
        Assert.False(provider.GetRequiredService<IOptions<JsonConsoleFormatterOptions>>().Value.JsonWriterOptions.Indented);
        Assert.Single(provider.GetServices<ILoggerProvider>());
        foreach (var category in new[] { "Microsoft.AspNetCore.Hosting.Diagnostics", "Microsoft.AspNetCore.Routing.EndpointMiddleware",
                     "Microsoft.AspNetCore.Http.Result.OkObjectResult", "Microsoft.EntityFrameworkCore.Database.Command" })
        {
            Assert.False(factory.CreateLogger(category).IsEnabled(LogLevel.Information), category);
            Assert.True(factory.CreateLogger(category).IsEnabled(LogLevel.Warning), category);
        }
        Assert.True(factory.CreateLogger("Estranged.Lfs.Api.Filters.BasicAuthFilter").IsEnabled(LogLevel.Information));
    }

    sealed class RecordingProvider : ILoggerProvider
    {
        public readonly List<(string Category, LogLevel Level, string Message, Exception Exception)> Entries = new();
        sealed class Recorder : ILogger
        {
            readonly RecordingProvider provider; readonly string category;
            public Recorder(RecordingProvider provider, string category) { this.provider = provider; this.category = category; }
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                lock (provider.Entries) provider.Entries.Add((category, logLevel, formatter(state, exception), exception));
            }
        }
        public ILogger CreateLogger(string categoryName) => new Recorder(this, categoryName);
        public void Dispose() { }
    }

    [Fact]
    public void AwsSdkForwardsOnlyWarningsAndErrorsIntoTheApplicationLog()
    {
        var provider = new RecordingProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider).SetMinimumLevel(LogLevel.Trace));
        AwsSdkLogging.Configure().Attach(factory);
        Assert.Equal(Amazon.LoggingOptions.SystemDiagnostics, Amazon.AWSConfigs.LoggingConfig.LogTo);
        var sdk = Amazon.Runtime.Internal.Util.Logger.GetLogger(typeof(Amazon.S3.AmazonS3Client));
        sdk.DebugFormat("RegionFinder resolved {0}", "us-east-1");
        sdk.InfoFormat("AWSSDKUtils computed {0}", "a value");
        var failure = new InvalidOperationException("storage failed");
        sdk.Error(failure, "Request to {0} failed", "s3");
        var entry = Assert.Single(provider.Entries);
        Assert.StartsWith("Amazon", entry.Category);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("Request to s3 failed", entry.Message);
        Assert.Same(failure, entry.Exception);
    }

    [Fact]
    public void AwsSdkNotFoundAnswersToExistenceChecksAreNotErrors()
    {
        var provider = new RecordingProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider).SetMinimumLevel(LogLevel.Trace));
        AwsSdkLogging.Configure().Attach(factory);
        var sdk = Amazon.Runtime.Internal.Util.Logger.GetLogger(typeof(Amazon.S3.AmazonS3Client));
        sdk.Error(new Amazon.S3.AmazonS3Exception("Not found", Amazon.Runtime.ErrorType.Sender, "NotFound", "request", System.Net.HttpStatusCode.NotFound),
            "AmazonS3Exception making request GetObjectMetadataRequest. Attempt 1.");
        var unavailable = new Amazon.S3.AmazonS3Exception("Slow down", Amazon.Runtime.ErrorType.Receiver, "SlowDown", "request", System.Net.HttpStatusCode.ServiceUnavailable);
        sdk.Error(unavailable, "AmazonS3Exception making request GetObjectMetadataRequest. Attempt 1.");
        lock (provider.Entries)
        {
            var entry = Assert.Single(provider.Entries);
            Assert.Same(unavailable, entry.Exception);
        }
    }

    [Fact]
    public async Task MissingAuthorizationIsTheLfsChallengeNotAnError()
    {
        var logger = new RecordingLogger<BasicAuthFilter>();
        var authenticator = new Mock<IAuthenticator>(MockBehavior.Strict);
        var http = new DefaultHttpContext(); http.Request.Method = "POST";
        var route = new RouteData(); route.Values["org"] = "AppMana"; route.Values["repo"] = "repo";
        var action = new ActionContext(http, route, new ActionDescriptor());
        var context = new ActionExecutingContext(action, new List<IFilterMetadata>(), new Dictionary<string, object>(), new object());
        var filter = new BasicAuthFilter(logger, authenticator.Object, Array.Empty<IRepositoryAuthenticator>());
        await filter.OnActionExecutionAsync(context, () => throw new InvalidOperationException("must not run"));
        Assert.Equal(401, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Warning || entry.Exception != null);

        http.Request.Headers.Authorization = "Bearer token";
        context.Result = null;
        await filter.OnActionExecutionAsync(context, () => throw new InvalidOperationException("must not run"));
        Assert.Equal(401, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }
}
