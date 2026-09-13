using Estranged.Lfs.Api.Entities;
using Estranged.Lfs.Api.Filters;
using Estranged.Lfs.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
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
}
