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
