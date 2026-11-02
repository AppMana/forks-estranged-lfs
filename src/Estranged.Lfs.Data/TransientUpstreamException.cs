using System;

namespace Estranged.Lfs.Data
{
    /// <summary>
    /// An upstream dependency (object storage, STS, the identity provider)
    /// failed transiently and server-side retries were exhausted. The client
    /// should retry the request later; it says nothing about the request.
    /// </summary>
    public sealed class TransientUpstreamException : Exception
    {
        public string Upstream { get; }

        public TransientUpstreamException(string upstream, Exception innerException)
            : base($"{upstream} is temporarily unavailable.", innerException)
        {
            Upstream = upstream;
        }
    }
}
