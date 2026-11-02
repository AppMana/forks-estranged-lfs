using Estranged.Lfs.Api.Entities;
using Estranged.Lfs.Api.Filters;
using Estranged.Lfs.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
namespace Estranged.Lfs.Registry.Tests;
public sealed class FilterTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BatchDownloadsUseReadPermissionAndRouteSelectsOnlyOneAuthenticator(bool uuid, bool upload)
    {
        var legacy = new Mock<IAuthenticator>(MockBehavior.Strict);
        var modern = new Mock<IRepositoryAuthenticator>(MockBehavior.Strict);
        var permission = upload ? LfsPermission.Write : LfsPermission.Read;
        if (uuid) modern.Setup(x => x.Authenticate("Basic dDpw", "repo", permission, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        else legacy.Setup(x => x.Authenticate("t", "p", "AppMana", "repo", permission, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var http = new DefaultHttpContext(); http.Request.Method = "POST"; http.Request.Headers.Authorization = "Basic dDpw";
        var route = new RouteData(); route.Values["org"] = uuid ? "r" : "AppMana"; route.Values["repo"] = "repo";
        var action = new ActionContext(http, route, new ActionDescriptor());
        var context = new ActionExecutingContext(action, new List<IFilterMetadata>(), new Dictionary<string, object> { ["request"] = new BatchRequest { Operation = upload ? LfsOperation.Upload : LfsOperation.Download } }, new object());
        var filter = new BasicAuthFilter(NullLogger<BasicAuthFilter>.Instance, legacy.Object, new[] { modern.Object });
        var next = false;
        await filter.OnActionExecutionAsync(context, () => { next = true; return Task.FromResult(new ActionExecutedContext(action, new List<IFilterMetadata>(), new object())); });
        Assert.True(next); legacy.VerifyAll(); modern.VerifyAll();
        if (uuid) legacy.VerifyNoOtherCalls(); else modern.VerifyNoOtherCalls();
    }

    sealed class TransientDatabaseException : System.Data.Common.DbException
    {
        public override bool IsTransient => true;
    }

    static (ActionExecutingContext Context, DefaultHttpContext Http) Context(bool uuid)
    {
        var http = new DefaultHttpContext(); http.Request.Method = "POST"; http.Request.Headers.Authorization = "Basic dDpw";
        var route = new RouteData(); route.Values["org"] = uuid ? "r" : "AppMana"; route.Values["repo"] = "repo";
        var action = new ActionContext(http, route, new ActionDescriptor());
        var context = new ActionExecutingContext(action, new List<IFilterMetadata>(), new Dictionary<string, object> { ["request"] = new BatchRequest { Operation = LfsOperation.Download } }, new object());
        return (context, http);
    }

    public static IEnumerable<object[]> TransientAuthenticationFailures() => new[]
    {
        new object[] { false, new TransientUpstreamException("Keycloak", new HttpRequestException("unavailable")) },
        new object[] { true, new TransientUpstreamException("identity issuer discovery", new HttpRequestException("unavailable")) },
        new object[] { true, new TransientDatabaseException() },
    };

    [Theory]
    [MemberData(nameof(TransientAuthenticationFailures))]
    public async Task TransientAuthenticationFailuresAskTheClientToRetryInsteadOfRejectingCredentials(bool uuid, Exception failure)
    {
        var legacy = new Mock<IAuthenticator>();
        var modern = new Mock<IRepositoryAuthenticator>();
        legacy.Setup(x => x.Authenticate(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<LfsPermission>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        modern.Setup(x => x.Authenticate(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<LfsPermission>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        var (context, http) = Context(uuid);
        var filter = new BasicAuthFilter(NullLogger<BasicAuthFilter>.Instance, legacy.Object, new[] { modern.Object });
        await filter.OnActionExecutionAsync(context, () => throw new InvalidOperationException("must not run"));
        Assert.Equal(429, Assert.IsAssignableFrom<IStatusCodeActionResult>(context.Result).StatusCode);
        Assert.True(int.Parse(http.Response.Headers.RetryAfter.ToString()) > 0);
    }

    [Theory]
    [InlineData(false, "invalid", 401)]
    [InlineData(false, "forbidden", 403)]
    [InlineData(true, "invalid", 401)]
    [InlineData(true, "forbidden", 403)]
    public async Task CredentialRejectionsStayUnauthorisedOrForbidden(bool uuid, string rejection, int status)
    {
        Exception failure = rejection == "forbidden" ? new UnauthorizedAccessException() :
            uuid ? new System.Security.Authentication.AuthenticationException() : new InvalidOperationException("Keycloak rejected client credentials");
        var legacy = new Mock<IAuthenticator>();
        var modern = new Mock<IRepositoryAuthenticator>();
        legacy.Setup(x => x.Authenticate(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<LfsPermission>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        modern.Setup(x => x.Authenticate(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<LfsPermission>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        var (context, http) = Context(uuid);
        var filter = new BasicAuthFilter(NullLogger<BasicAuthFilter>.Instance, legacy.Object, new[] { modern.Object });
        await filter.OnActionExecutionAsync(context, () => throw new InvalidOperationException("must not run"));
        Assert.Equal(status, Assert.IsAssignableFrom<IStatusCodeActionResult>(context.Result).StatusCode);
        Assert.False(http.Response.Headers.ContainsKey("Retry-After"));
    }
}
