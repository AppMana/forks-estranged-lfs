using Estranged.Lfs.Data;
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Estranged.Lfs.Tests.Data
{
    public class TransientRetryTests
    {
        static readonly TransientRetryPolicy Fast = new() { MaxAttempts = 3, InitialDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero };

        [Fact]
        public async Task TransientFailuresAreRetriedUntilSuccess()
        {
            var calls = 0;
            var result = await TransientRetry.ExecuteAsync("storage", Fast, TransientRetry.IsTransientNetworkFailure, _ =>
            {
                if (++calls < 3) throw new HttpRequestException("reset");
                return Task.FromResult(42);
            }, CancellationToken.None);
            Assert.Equal(42, result);
            Assert.Equal(3, calls);
        }

        [Fact]
        public async Task ExhaustedTransientFailuresBecomeTransientUpstreamException()
        {
            var calls = 0;
            var failure = await Assert.ThrowsAsync<TransientUpstreamException>(() => TransientRetry.ExecuteAsync<int>("storage", Fast, TransientRetry.IsTransientNetworkFailure, _ =>
            {
                calls++;
                throw new HttpRequestException("unavailable", null, HttpStatusCode.ServiceUnavailable);
            }, CancellationToken.None));
            Assert.Equal(3, calls);
            Assert.Equal("storage", failure.Upstream);
            Assert.IsType<HttpRequestException>(failure.InnerException);
        }

        [Fact]
        public async Task DefinitiveFailuresAreNotRetried()
        {
            var calls = 0;
            await Assert.ThrowsAsync<HttpRequestException>(() => TransientRetry.ExecuteAsync<int>("storage", Fast, TransientRetry.IsTransientNetworkFailure, _ =>
            {
                calls++;
                throw new HttpRequestException("not found", null, HttpStatusCode.NotFound);
            }, CancellationToken.None));
            Assert.Equal(1, calls);
        }

        [Fact]
        public async Task AttemptTimeoutsAreRetriedButCallerCancellationIsNot()
        {
            var calls = 0;
            var policy = new TransientRetryPolicy { MaxAttempts = 2, InitialDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero, AttemptTimeout = TimeSpan.FromMilliseconds(20) };
            await Assert.ThrowsAsync<TransientUpstreamException>(() => TransientRetry.ExecuteAsync<int>("storage", policy, _ => false, async token =>
            {
                calls++;
                await Task.Delay(Timeout.Infinite, token);
                return 0;
            }, CancellationToken.None));
            Assert.Equal(2, calls);

            calls = 0;
            using var caller = new CancellationTokenSource();
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TransientRetry.ExecuteAsync<int>("storage", Fast, _ => true, token =>
            {
                calls++;
                token.ThrowIfCancellationRequested();
                return Task.FromResult(0);
            }, caller.Token));
            Assert.Equal(1, calls);
        }

        [Fact]
        public async Task NestedExhaustionIsNotRetriedAgain()
        {
            var calls = 0;
            await Assert.ThrowsAsync<TransientUpstreamException>(() => TransientRetry.ExecuteAsync<int>("outer", Fast, _ => true, _ =>
            {
                calls++;
                throw new TransientUpstreamException("inner", new HttpRequestException());
            }, CancellationToken.None));
            Assert.Equal(1, calls);
        }

        [Theory]
        [InlineData(408, true)]
        [InlineData(429, true)]
        [InlineData(500, true)]
        [InlineData(503, true)]
        [InlineData(400, false)]
        [InlineData(401, false)]
        [InlineData(403, false)]
        [InlineData(404, false)]
        public void StatusClassification(int status, bool transient) => Assert.Equal(transient, TransientRetry.IsTransientStatus(status));
    }
}
