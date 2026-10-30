using System;
using Formula9.Backend;
using NUnit.Framework;

namespace Formula9.Tests
{
    public class BackendEndpointTests
    {
        [Test]
        public void ApiUrlKeepsTheBasePathPrefix()
        {
            Assert.AreEqual("http://host:9000/proxy/api/v2/sessions",
                BackendEndpoint.ApiUrl(new Uri("http://host:9000/proxy/"), "/api/v2/sessions").ToString());
            Assert.AreEqual("http://127.0.0.1:8000/api/auth/login",
                BackendEndpoint.ApiUrl(new Uri(BackendEndpoint.DefaultUrl), "/api/auth/login").ToString());
        }

        [Test]
        public void ApiUrlKeepsTheQueryOutOfThePath()
        {
            var url = BackendEndpoint.ApiUrl(new Uri("http://host:9000/proxy"), "/api/v2/sessions/s-1/events?after=7&limit=100");
            Assert.AreEqual("/proxy/api/v2/sessions/s-1/events", url.AbsolutePath);
            Assert.AreEqual("?after=7&limit=100", url.Query);
        }

        [Test]
        public void ApiUrlRejectsRelativePaths()
        {
            Assert.Throws<ArgumentException>(() => BackendEndpoint.ApiUrl(new Uri(BackendEndpoint.DefaultUrl), "api/v2"));
        }
    }
}
