using System;

namespace Formula9.Backend
{
    // Where the Formula_9 FastAPI backend lives. Same rule as the Qt client's BackendEndpoint:
    // FORMULA9_BACKEND_URL overrides the local default.
    public static class BackendEndpoint
    {
        public const string DefaultUrl = "http://127.0.0.1:8000";

        public static Uri ConfiguredUrl()
        {
            var configured = Environment.GetEnvironmentVariable("FORMULA9_BACKEND_URL");
            return new Uri(string.IsNullOrWhiteSpace(configured) ? DefaultUrl : configured.Trim());
        }

        // Joins an API path onto the base URL, keeping any path prefix the base already has.
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
