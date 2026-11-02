using Estranged.Lfs.Data;
using Microsoft.AspNetCore.Http;
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Data.Common;

namespace Estranged.Lfs.Api.Filters
{
    public class BasicAuthFilter : IAsyncActionFilter
    {
        private readonly ILogger<BasicAuthFilter> logger;
        private readonly IAuthenticator authenticator;
        private readonly IRepositoryAuthenticator repositoryAuthenticator;
        private readonly LfsBatchOptions options;

        public BasicAuthFilter(ILogger<BasicAuthFilter> logger, IAuthenticator authenticator, System.Collections.Generic.IEnumerable<IRepositoryAuthenticator> repositoryAuthenticators,
            LfsBatchOptions options = null)
        {
            this.logger = logger;
            this.authenticator = authenticator;
            repositoryAuthenticator = repositoryAuthenticators.SingleOrDefault();
            this.options = options ?? new LfsBatchOptions();
        }

        public string AuthorizationHeader => "Authorization";
        public string BasicPrefix => "Basic";

        private void Unauthorised(ActionExecutingContext context)
        {
            context.Result = new StatusCodeResult(401);
        }

        private void Forbidden(ActionExecutingContext context)
        {
            context.Result = new StatusCodeResult(403);
        }

        // An identity provider, issuer or registry database that is briefly
        // unavailable says nothing about the credentials, so the client is
        // asked to retry rather than told its credentials are wrong.
        private void RetryLater(ActionExecutingContext context, Exception exception, string authenticatorName)
        {
            logger.LogWarning(exception, "{Authenticator} is temporarily unavailable for {Org}/{Repo}; asking the client to retry",
                authenticatorName, context.RouteData.Values["org"], context.RouteData.Values["repo"]);
            context.Result = LfsResults.RetryLater(context.HttpContext, options, "Authentication is temporarily unavailable.");
        }

        private static bool IsTransient(Exception exception) =>
            exception is TransientUpstreamException || exception is DbException database && database.IsTransient;

        private (string Username, string Password) GetCredentials(IHeaderDictionary headers)
        {
            if (!headers.ContainsKey(AuthorizationHeader))
            {
                throw new InvalidOperationException("No Authorization header found.");
            }

            string[] authValues = headers[AuthorizationHeader].ToArray();
            if (authValues.Length != 1)
            {
                throw new InvalidOperationException("More than one Authorization header found.");
            }

            string auth = authValues.Single();
            if (!auth.StartsWith(BasicPrefix))
            {
                throw new InvalidOperationException("Authorization header is not Basic.");
            }

            auth = auth.Substring(BasicPrefix.Length).Trim();

            byte[] decoded = Convert.FromBase64String(auth);
            Encoding iso = Encoding.GetEncoding("ISO-8859-1");

            string[] authPair = iso.GetString(decoded).Split(new[] { ':' }, 2);
            if (authPair.Length != 2)
            {
                throw new InvalidOperationException("Authorization header does not contain username and password.");
            }

            return (authPair[0], authPair[1]);
        }

        private LfsPermission GetRequiredPermission(ActionExecutingContext context) =>
            context.HttpContext.Request.Method == "GET" ||
            context.ActionArguments.TryGetValue("request", out var request) && request is Entities.BatchRequest batch && batch.Operation == Entities.LfsOperation.Download
                ? LfsPermission.Read : LfsPermission.Write;

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            if (context.RouteData.Values.TryGetValue("org", out var route) && route?.ToString() == "r")
            {
                try
                {
                    if (repositoryAuthenticator == null) throw new UnauthorizedAccessException("UUID repository authentication is not configured.");
                    await repositoryAuthenticator.Authenticate(context.HttpContext.Request.Headers.Authorization.ToString(),
                        context.RouteData.Values["repo"]?.ToString(), GetRequiredPermission(context), context.HttpContext.RequestAborted);
                }
                catch (UnauthorizedAccessException) { Forbidden(context); return; }
                catch (System.Security.Authentication.AuthenticationException) { Unauthorised(context); return; }
                catch (Exception e) when (IsTransient(e)) { RetryLater(context, e, repositoryAuthenticator.GetType().Name); return; }
                await next();
                return;
            }
            if (!context.HttpContext.Request.Headers.ContainsKey(AuthorizationHeader))
            {
                // Git LFS sends credentials only after this 401 challenge.
                logger.LogDebug("Request has no Authorization header; challenging");
                Unauthorised(context);
                return;
            }

            string username;
            string password;
            try
            {
                (username, password) = GetCredentials(context.HttpContext.Request.Headers);
            }
            catch (Exception e) when (e is InvalidOperationException || e is FormatException)
            {
                logger.LogWarning("Rejected malformed Basic credentials: {Reason}", e.Message);
                Unauthorised(context);
                return;
            }

            try
            {
                string organisation = context.RouteData.Values.TryGetValue("org", out object org) ? org?.ToString() : null;
                string repository = context.RouteData.Values.TryGetValue("repo", out object repo) ? repo?.ToString() : null;
                await authenticator.Authenticate(username, password, organisation, repository, GetRequiredPermission(context), context.HttpContext.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception e) when (IsTransient(e))
            {
                RetryLater(context, e, authenticator.GetType().Name);
                return;
            }
            catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested)
            {
                logger.LogDebug("Client disconnected during authentication");
                context.Result = new StatusCodeResult(StatusCodes.Status499ClientClosedRequest);
                return;
            }
            catch (UnauthorizedAccessException e)
            {
                logger.LogError(e, $"Forbidden by {authenticator.GetType().Name}");
                Forbidden(context);
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, $"Error from {authenticator.GetType().Name}");
                Unauthorised(context);
                return;
            }

            await next();
        }
    }
}
