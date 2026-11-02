using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Estranged.Lfs.Data
{
    public sealed class TransientRetryPolicy
    {
        public int MaxAttempts { get; init; } = 3;
        public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(200);
        public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(2);
        public TimeSpan? AttemptTimeout { get; init; }
    }

    public static class TransientRetry
    {
        public static bool IsTransientStatus(int status) => status == 408 || status == 429 || status >= 500;

        /// <summary>
        /// A connection, stream or timeout failure, or an HTTP response whose
        /// status means "try again". HTTP answers such as 400, 401, 403 and
        /// 404 are definitive.
        /// </summary>
        public static bool IsTransientNetworkFailure(Exception exception) => exception switch
        {
            HttpRequestException http => http.StatusCode == null || IsTransientStatus((int)http.StatusCode),
            IOException => true,
            SocketException => true,
            TimeoutException => true,
            _ => false,
        };

        /// <summary>
        /// Runs <paramref name="operation"/> up to <see cref="TransientRetryPolicy.MaxAttempts"/>
        /// times with exponential backoff while it fails transiently, inside the
        /// caller's <paramref name="token"/>. A cancellation that did not come
        /// from the caller (an attempt or client timeout) counts as transient.
        /// When attempts run out the last failure is wrapped in
        /// <see cref="TransientUpstreamException"/>.
        /// </summary>
        public static async Task<T> ExecuteAsync<T>(string upstream, TransientRetryPolicy policy, Func<Exception, bool> isTransient,
            Func<CancellationToken, Task<T>> operation, CancellationToken token)
        {
            var delay = policy.InitialDelay;
            for (var attempt = 1; ; attempt++)
            {
                using var attemptToken = CancellationTokenSource.CreateLinkedTokenSource(token);
                if (policy.AttemptTimeout is TimeSpan timeout) attemptToken.CancelAfter(timeout);
                Exception failure;
                try
                {
                    return await operation(attemptToken.Token).ConfigureAwait(false);
                }
                catch (TransientUpstreamException)
                {
                    throw;
                }
                catch (Exception exception) when (!token.IsCancellationRequested &&
                                                  (exception is OperationCanceledException || isTransient(exception)))
                {
                    failure = exception;
                }

                if (attempt >= policy.MaxAttempts) throw new TransientUpstreamException(upstream, failure);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, token).ConfigureAwait(false);
                var doubled = TimeSpan.FromTicks(delay.Ticks * 2);
                delay = doubled < policy.MaxDelay ? doubled : policy.MaxDelay;
            }
        }
    }
}
