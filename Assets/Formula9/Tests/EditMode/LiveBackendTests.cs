/*
 * Tests UnityHttpTransport against a running backend.
 *
 * Author: Choo Jian Wei
 * Date Created: 30/10/2026
 * Last Modified: 30/10/2026
 *
 * Description:
 *     Skipped when no backend is reachable. Uses no real account: only replies that need no valid
 *     login are checked. UnityWebRequest completes on the editor's update loop, so these are
 *     coroutine tests that yield while waiting; blocking on the task would stop the request from
 *     ever finishing.
 */

using System;
using System.Collections;
using System.Threading.Tasks;
using Formula9.Backend;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Formula9.Tests
{
    // -----------------------------------------------------------------------------------
    // Class: LiveBackendTests
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class tests real HTTP replies from a running backend (category LiveBackend).
    [Category("LiveBackend")]
    public class LiveBackendTests
    {
        private readonly IHttpTransport transport = new UnityHttpTransport();

        // -----------------------------------------------------------------------------------
        // Function: Wait
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function yields until a task finishes, failing the test after a deadline.
        //
        // Args:
        //     task (Task): The task.
        //     seconds (double): The deadline.
        //
        // Returns:
        //     IEnumerator: The wait.
        private static IEnumerator Wait(Task task, double seconds = 15)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (!task.IsCompleted)
            {
                if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting for the backend");
                yield return null;
            }
        }

        // -----------------------------------------------------------------------------------
        // Function: RequireBackend
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function skips the test when no backend answers at the configured address.
        //
        // Returns:
        //     IEnumerator: The probe.
        private IEnumerator RequireBackend()
        {
            var probe = transport.SendAsync(new HttpRequest
            {
                Url = BackendEndpoint.ApiUrl(BackendEndpoint.ConfiguredUrl(), "/api/auth/me"),
                TimeoutSeconds = 3,
            });
            yield return Wait(probe);
            if (!probe.Result.Complete) Assert.Ignore("No Formula_9 backend at " + BackendEndpoint.ConfiguredUrl());
        }

        // -----------------------------------------------------------------------------------
        // Function: WrongPasswordIsRejectedWithItsCode
        // -----------------------------------------------------------------------------------
        // Description:
        //     A wrong password is refused with 401 invalid_credentials, and no login starts.
        [UnityTest]
        public IEnumerator WrongPasswordIsRejectedWithItsCode()
        {
            yield return RequireBackend();
            var session = new AuthSession();
            var login = new AuthClient(transport, session).LogInAsync("unity-client-probe", "definitely-wrong");
            yield return Wait(login);
            var error = login.Exception?.InnerException as BackendErrorException;
            Assert.IsNotNull(error, login.Exception?.ToString() ?? "login unexpectedly succeeded");
            Assert.AreEqual(401, error.Error.Status);
            Assert.AreEqual("invalid_credentials", error.Error.Code);
            Assert.IsFalse(session.IsLoggedIn);
        }

        // -----------------------------------------------------------------------------------
        // Function: UnknownTokenEndsTheLogin
        // -----------------------------------------------------------------------------------
        // Description:
        //     A training request with an unknown token is refused as not logged in, and the login
        //     ends.
        [UnityTest]
        public IEnumerator UnknownTokenEndsTheLogin()
        {
            yield return RequireBackend();
            var session = new AuthSession();
            session.Begin("unity-client-probe", "not-a-real-token", 3600);
            var list = new TrainingApi(transport, session).ListSessionsAsync();
            yield return Wait(list);
            Assert.IsInstanceOf<NotLoggedInException>(list.Exception?.InnerException, list.Exception?.ToString());
            Assert.IsFalse(session.IsLoggedIn);
        }
    }
}
