/*
 * Unit tests for BackendEndpoint.cs.
 *
 * Author: Choo Jian Wei
 * Date Created: 30/10/2026
 * Last Modified: 30/10/2026
 *
 * Description:
 *     Checks how API URLs are built on the backend address: a base path prefix is kept, a query
 *     stays out of the path, and relative paths are refused.
 */

using System;
using Formula9.Backend;
using NUnit.Framework;

namespace Formula9.Tests
{
    // -----------------------------------------------------------------------------------
    // Class: BackendEndpointTests
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class tests API URL building.
    public class BackendEndpointTests
    {
        // -----------------------------------------------------------------------------------
        // Function: ApiUrlKeepsTheBasePathPrefix
        // -----------------------------------------------------------------------------------
        // Description:
        //     An API path is appended to the base URL's own path, for example behind a proxy.
        [Test]
        public void ApiUrlKeepsTheBasePathPrefix()
        {
            Assert.AreEqual("http://host:9000/proxy/api/v2/sessions",
                BackendEndpoint.ApiUrl(new Uri("http://host:9000/proxy/"), "/api/v2/sessions").ToString());
            Assert.AreEqual("http://127.0.0.1:8000/api/auth/login",
                BackendEndpoint.ApiUrl(new Uri(BackendEndpoint.DefaultUrl), "/api/auth/login").ToString());
        }

        // -----------------------------------------------------------------------------------
        // Function: ApiUrlKeepsTheQueryOutOfThePath
        // -----------------------------------------------------------------------------------
        // Description:
        //     A query becomes the URL's query instead of an escaped part of the path.
        [Test]
        public void ApiUrlKeepsTheQueryOutOfThePath()
        {
            var url = BackendEndpoint.ApiUrl(new Uri("http://host:9000/proxy"), "/api/v2/sessions/s-1/events?after=7&limit=100");
            Assert.AreEqual("/proxy/api/v2/sessions/s-1/events", url.AbsolutePath);
            Assert.AreEqual("?after=7&limit=100", url.Query);
        }

        // -----------------------------------------------------------------------------------
        // Function: ApiUrlRejectsRelativePaths
        // -----------------------------------------------------------------------------------
        // Description:
        //     A path that does not start with '/' is refused.
        [Test]
        public void ApiUrlRejectsRelativePaths()
        {
            Assert.Throws<ArgumentException>(() => BackendEndpoint.ApiUrl(new Uri(BackendEndpoint.DefaultUrl), "api/v2"));
        }
    }
}
