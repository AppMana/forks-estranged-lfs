using Estranged.Lfs.Api.Entities;
using Estranged.Lfs.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Estranged.Lfs.Api.Controllers
{
    [Route("{org}/{repo}/objects")]
    public class ObjectsController : ControllerBase
    {
        private readonly IObjectManager objectManager;
        private readonly ILogger<ObjectsController> logger;
        private readonly LfsBatchOptions options;

        public ObjectsController(IObjectManager objectManager, ILogger<ObjectsController> logger, LfsBatchOptions options = null)
        {
            this.objectManager = objectManager;
            this.logger = logger;
            this.options = options ?? new LfsBatchOptions();
        }

        [HttpPost("batch")]
        public async Task<ActionResult<BatchResponse>> BatchAsync(string org, string repo, [FromBody] BatchRequest request)
        {
            if (request?.Objects == null || request.Operation != LfsOperation.Upload && request.Operation != LfsOperation.Download)
            {
                return LfsResults.Error(StatusCodes.Status422UnprocessableEntity, "A batch request needs an upload or download operation and a list of objects.");
            }

            string operation = request.Operation == LfsOperation.Upload ? "upload" : "download";
            int objectCount = request.Objects.Count;
            CancellationToken aborted = HttpContext.RequestAborted;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(aborted);
            budget.CancelAfter(options.RequestBudget);
            try
            {
                var objects = request.Operation == LfsOperation.Upload
                    ? await objectManager.UploadObjects(request.Objects, budget.Token).ConfigureAwait(false)
                    : await objectManager.DownloadObjects(request.Objects, budget.Token).ConfigureAwait(false);
                // Only basic transfer actions are issued.
                return new BatchResponse { Transfer = "basic", Objects = objects };
            }
            catch (TransientUpstreamException exception)
            {
                logger.LogWarning(exception, "Batch {Operation} of {ObjectCount} objects for {Org}/{Repo} could not reach {Upstream}; asking the client to retry",
                    operation, objectCount, org, repo, exception.Upstream);
                return LfsResults.RetryLater(HttpContext, options, "Storage is temporarily unavailable.");
            }
            catch (OperationCanceledException exception) when (budget.IsCancellationRequested && !aborted.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Batch {Operation} of {ObjectCount} objects for {Org}/{Repo} exceeded its {Budget} budget; asking the client to retry",
                    operation, objectCount, org, repo, options.RequestBudget);
                return LfsResults.RetryLater(HttpContext, options, "Storage did not answer in time.");
            }
            catch (OperationCanceledException) when (aborted.IsCancellationRequested)
            {
                logger.LogDebug("Client disconnected during batch {Operation} of {ObjectCount} objects for {Org}/{Repo}", operation, objectCount, org, repo);
                return StatusCode(StatusCodes.Status499ClientClosedRequest);
            }
            catch (UnauthorizedAccessException exception)
            {
                logger.LogWarning(exception, "Batch {Operation} of {ObjectCount} objects for {Org}/{Repo} was denied by repository storage",
                    operation, objectCount, org, repo);
                return LfsResults.Error(StatusCodes.Status403Forbidden, "Repository storage denied access.");
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Batch {Operation} of {ObjectCount} objects for {Org}/{Repo} failed",
                    operation, objectCount, org, repo);
                throw;
            }
        }
    }
}
