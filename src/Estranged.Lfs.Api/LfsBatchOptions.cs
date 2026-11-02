using System;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Estranged.Lfs.Api
{
    public sealed class LfsBatchOptions
    {
        /// <summary>Time a batch request may spend on storage lookups.</summary>
        public TimeSpan RequestBudget { get; init; } = TimeSpan.FromSeconds(25);

        /// <summary>Retry-After sent when an upstream dependency is temporarily unavailable.</summary>
        public TimeSpan RetryAfter { get; init; } = TimeSpan.FromSeconds(10);
    }

    public static class LfsResults
    {
        /// <summary>
        /// Git LFS retries a whole batch on 429 after Retry-After. Every other
        /// 5xx except 501, 507 and 509 fails the batch, and per-object errors
        /// are never retried, so transient conditions are reported this way.
        /// </summary>
        public static ObjectResult RetryLater(HttpContext http, LfsBatchOptions options, string message)
        {
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(options.RetryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return Error(StatusCodes.Status429TooManyRequests, message);
        }

        public static ObjectResult Error(int status, string message) =>
            new ObjectResult(new { message }) { StatusCode = status };
    }
}
