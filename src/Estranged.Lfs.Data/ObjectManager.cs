using Estranged.Lfs.Data.Entities;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Estranged.Lfs.Data
{
    public class ObjectManager : IObjectManager
    {
        public const int MaxConcurrentDownloadLookups = 16;

        private readonly IBlobAdapter blobAdapter;

        public ObjectManager(IBlobAdapter blobAdapter)
        {
            this.blobAdapter = blobAdapter;
        }

        /// <summary>
        /// Looks up every object with bounded concurrency, keeping request order.
        /// An invalid object becomes a 422 for that object. Any other failure
        /// fails the batch: the remaining lookups are cancelled and the failure
        /// that stopped the batch is rethrown rather than those cancellations.
        /// </summary>
        private static async Task<SignedBlob[]> LookupAll(IList<RequestObject> objects, System.Func<RequestObject, CancellationToken, Task<SignedBlob>> lookup, CancellationToken token)
        {
            using var batch = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var concurrency = new SemaphoreSlim(MaxConcurrentDownloadLookups);
            var lookups = objects.Select(async ob =>
            {
                await concurrency.WaitAsync(batch.Token).ConfigureAwait(false);
                try
                {
                    return await lookup(ob, batch.Token).ConfigureAwait(false);
                }
                catch (System.ArgumentException e)
                {
                    return new SignedBlob { ErrorCode = 422, ErrorMessage = e.Message };
                }
                catch
                {
                    batch.Cancel();
                    throw;
                }
                finally
                {
                    concurrency.Release();
                }
            }).ToArray();

            try
            {
                return await Task.WhenAll(lookups).ConfigureAwait(false);
            }
            catch (System.Exception) when (lookups.FirstOrDefault(x => x.IsFaulted && x.Exception.InnerException is not System.OperationCanceledException) is Task<SignedBlob> failed)
            {
                ExceptionDispatchInfo.Capture(failed.Exception.InnerException).Throw();
                throw;
            }
        }

        public async Task<IEnumerable<ResponseObject>> UploadObjects(IList<RequestObject> objects, CancellationToken token)
        {
            SignedBlob[] signedBlobs = await LookupAll(objects, (ob, t) => blobAdapter.UriForUpload(ob.Oid, ob.Size, t), token).ConfigureAwait(false);

            return objects.Select((ob, index) => signedBlobs[index].ErrorCode.HasValue
                ? new ResponseObject
                {
                    Oid = ob.Oid,
                    Size = ob.Size,
                    Actions = null,
                    Error = new ResponseObjectError
                    {
                        Code = signedBlobs[index].ErrorCode.Value,
                        Message = signedBlobs[index].ErrorMessage
                    }
                }
                : new ResponseObject
                {
                    Oid = ob.Oid,
                    Size = ob.Size,
                    Authenticated = true,
                    Actions = signedBlobs[index].AlreadyExists || signedBlobs[index].Uri == null
                        ? null
                        : new Actions
                        {
                            Upload = new Action
                            {
                                Href = signedBlobs[index].Uri,
                                ExpiresIn = (long)signedBlobs[index].Expiry.TotalSeconds,
                                Headers = signedBlobs[index].Headers
                            }
                        }
                }).ToList();
        }

        public async Task<IEnumerable<ResponseObject>> DownloadObjects(IList<RequestObject> objects, CancellationToken token)
        {
            // Each download needs a storage metadata round trip. Awaiting them one
            // at a time made a 100-object batch cost the sum of its latencies and
            // overrun the controller's 25 second budget; bound the fan-out instead.
            var signedBlobs = await LookupAll(objects, (x, t) => blobAdapter.UriForDownload(x.Oid, t), token).ConfigureAwait(false);

            var responseObjects = new List<ResponseObject>();
            foreach ((RequestObject requestObject, SignedBlob signedBlob) in objects.Zip(signedBlobs))
            {
                if (signedBlob.ErrorCode.HasValue)
                {
                    responseObjects.Add(new ResponseObject
                    {
                        Oid = requestObject.Oid,
                        Size = requestObject.Size,
                        Error = new ResponseObjectError
                        {
                            Code = signedBlob.ErrorCode.Value,
                            Message = signedBlob.ErrorMessage
                        }
                    });
                }
                else
                {
                    responseObjects.Add(new ResponseObject
                    {
                        Oid = requestObject.Oid,
                        Size = signedBlob.Size.Value,
                        Authenticated = true,
                        Actions = new Actions
                        {
                            Download = new Action
                            {
                                Href = signedBlob.Uri,
                                ExpiresIn = (long)signedBlob.Expiry.TotalSeconds,
                                Headers = signedBlob.Headers
                            }
                        }
                    });
                }
            }

            return responseObjects;
        }
    }
}
