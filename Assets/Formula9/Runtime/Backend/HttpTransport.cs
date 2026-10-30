/*
 * Sends HTTP requests to the backend.
 *
 * Author: Choo Jian Wei
 * Date Created: 30/10/2026
 * Last Modified: 30/10/2026
 *
 * Description:
 *     Part of the Unity client's backend layer. Requests and replies are plain classes, free of
 *     UnityWebRequest, so tests can fake the backend. A transport never throws for network trouble:
 *     it reports it as a Failure, so callers can tell "the backend said no" from "we do not know
 *     what happened".
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace Formula9.Backend
{
    // -----------------------------------------------------------------------------------
    // Class: HttpRequest
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class is one request for an IHttpTransport.
    //
    //     Attributes: Method, Url, Body (JSON, or null) and Headers; TimeoutSeconds, 10 by default,
    //     the same bound as the Qt client's setTransferTimeout(10000).
    public sealed class HttpRequest
    {
        public string Method = "GET";
        public Uri Url;
        public string Body;
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>();
        // Same bound as the Qt client's setTransferTimeout(10000).
        public int TimeoutSeconds = 10;
    }

    // -----------------------------------------------------------------------------------
    // Class: HttpFailure
    // -----------------------------------------------------------------------------------
    // Description:
    //     This enum says why a request produced no usable reply. None means the backend answered,
    //     with any status. Incomplete means no complete reply arrived (connection refused or
    //     dropped, timeout, or an interrupted body), so the request may still have reached the
    //     backend. Cancelled means the caller cancelled it.
    public enum HttpFailure
    {
        None,
        // No complete reply: connection refused or dropped, timeout, or an interrupted body.
        // The request may still have reached the backend.
        Incomplete,
        Cancelled,
    }

    // -----------------------------------------------------------------------------------
    // Class: HttpResponse
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class is what came back: the status and body of a complete reply, or the Failure
    //     that prevented one.
    //
    //     Attributes: Status, Body (text), Data (the raw bytes, for binary downloads such as scene
    //     assets), Failure, and Complete, which is true when Failure is None.
    public sealed class HttpResponse
    {
        public int Status;
        public string Body;
        // The raw reply, for binary downloads such as scene assets.
        public byte[] Data;
        public HttpFailure Failure;

        public bool Complete => Failure == HttpFailure.None;
    }

    // -----------------------------------------------------------------------------------
    // Class: IHttpTransport
    // -----------------------------------------------------------------------------------
    // Description:
    //     This interface is one HTTP exchange. Implementations report network trouble as a Failure
    //     instead of throwing.
    public interface IHttpTransport
    {
        Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellation = default);
    }

    // -----------------------------------------------------------------------------------
    // Class: UnityHttpTransport
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class sends requests with UnityWebRequest. Call it from the main thread, as Unity
    //     requires.
    public sealed class UnityHttpTransport : IHttpTransport
    {
        // -----------------------------------------------------------------------------------
        // Function: SendAsync
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function sends one request. Redirects are not followed. A 4xx or 5xx reply still
        //     carries a complete status and body, so it counts as complete; anything else that ends
        //     without a status means the reply never arrived whole.
        //
        // Args:
        //     request (HttpRequest): The request to send.
        //     cancellation (CancellationToken): Cancels the request.
        //
        // Returns:
        //     Task<HttpResponse>: The reply, or the failure that prevented one. It never throws for
        //         network trouble.
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
