using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace Formula9.Backend
{
    // One request for an IHttpTransport, kept free of UnityWebRequest so tests can fake the backend.
    public sealed class HttpRequest
    {
        public string Method = "GET";
        public Uri Url;
        public string Body;
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>();
        // Same bound as the Qt client's setTransferTimeout(10000).
        public int TimeoutSeconds = 10;
    }

    // Why a request produced no usable reply; None when the backend answered (with any status).
    public enum HttpFailure
    {
        None,
        // No complete reply: connection refused or dropped, timeout, or an interrupted body.
        // The request may still have reached the backend.
        Incomplete,
        Cancelled,
    }

    // What came back: the status and body of a complete reply, or the Failure that prevented one.
    public sealed class HttpResponse
    {
        public int Status;
        public string Body;
        // The raw reply, for binary downloads such as scene assets.
        public byte[] Data;
        public HttpFailure Failure;

        public bool Complete => Failure == HttpFailure.None;
    }

    // One HTTP exchange. Implementations never throw for network trouble; they report it as
    // Failure so callers can tell "the backend said no" from "we do not know what happened".
    public interface IHttpTransport
    {
        Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellation = default);
    }

    // UnityWebRequest transport. Call it from the main thread, as Unity requires.
    public sealed class UnityHttpTransport : IHttpTransport
    {
        public Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellation = default)
        {
            var web = new UnityWebRequest(request.Url, request.Method)
            {
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = request.TimeoutSeconds,
                redirectLimit = 0,
            };
            if (request.Body != null)
            {
                web.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(request.Body));
                web.SetRequestHeader("Content-Type", "application/json");
            }
            foreach (var header in request.Headers) web.SetRequestHeader(header.Key, header.Value);

            var completion = new TaskCompletionSource<HttpResponse>();
            var registration = cancellation.Register(() => web.Abort());
            web.SendWebRequest().completed += _ =>
            {
                registration.Dispose();
                var response = new HttpResponse
                {
                    Status = (int)web.responseCode, Body = web.downloadHandler?.text, Data = web.downloadHandler?.data,
                };
                if (cancellation.IsCancellationRequested) response.Failure = HttpFailure.Cancelled;
                // A ProtocolError still carries a complete status and body (4xx/5xx); anything
                // else means the reply never arrived whole.
                else if (web.result == UnityWebRequest.Result.ConnectionError || web.result == UnityWebRequest.Result.DataProcessingError
                         || web.responseCode == 0) response.Failure = HttpFailure.Incomplete;
                web.Dispose();
                completion.TrySetResult(response);
            };
            return completion.Task;
        }
    }
}
