/*
 * Where the training backend lives.
 *
 * Author: Choo Jian Wei
 * Date Created: 30/10/2026
 * Last Modified: 30/10/2026
 *
 * Description:
 *     Part of the Unity client's backend layer. Same rule as the Qt client's BackendEndpoint:
 *     FORMULA9_BACKEND_URL overrides the local default, http://127.0.0.1:8000.
 */

using System;

namespace Formula9.Backend
{
    // -----------------------------------------------------------------------------------
    // Class: BackendEndpoint
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class works out the backend's address and builds API URLs on it.
    //
    //     Attributes: DefaultUrl is the local backend's address.
    public static class BackendEndpoint
    {
        public const string DefaultUrl = "http://127.0.0.1:8000";

        // -----------------------------------------------------------------------------------
        // Function: ConfiguredUrl
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function returns the backend address: FORMULA9_BACKEND_URL when it is set,
        //     otherwise DefaultUrl.
        //
        // Returns:
        //     Uri: The backend's base URL.
        public static Uri ConfiguredUrl()
        {
            var configured = Environment.GetEnvironmentVariable("FORMULA9_BACKEND_URL");
            return new Uri(string.IsNullOrWhiteSpace(configured) ? DefaultUrl : configured.Trim());
        }

        // -----------------------------------------------------------------------------------
        // Function: ApiUrl
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function joins an API path onto the base URL, keeping any path prefix the base
        //     already has. A query in the path is kept as the URL's query; it must not become part
        //     of the path, where UriBuilder would escape its '?'.
        //
        // Args:
        //     baseUrl (Uri): The backend's base URL.
        //     path (string): The API path, starting with '/', optionally with a query.
        //
        // Returns:
        //     Uri: The full URL.
        //
        // Throws:
        //     ArgumentNullException: If baseUrl is null.
        //     ArgumentException: If path does not start with '/'.
        public static Uri ApiUrl(Uri baseUrl, string path)
        {
            if (baseUrl == null) throw new ArgumentNullException(nameof(baseUrl));
            if (string.IsNullOrEmpty(path) || path[0] != '/') throw new ArgumentException("API paths start with '/'.", nameof(path));
            // A query must not become part of the path, where UriBuilder would escape its '?'.
            var query = path.IndexOf('?');
            var builder = new UriBuilder(baseUrl)
            {
                Path = baseUrl.AbsolutePath.TrimEnd('/') + (query < 0 ? path : path.Substring(0, query)),
                Query = query < 0 ? "" : path.Substring(query + 1),
            };
            return builder.Uri;
        }
    }
}
