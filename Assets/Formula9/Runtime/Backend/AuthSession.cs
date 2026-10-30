/*
 * The logged-in user and their login token.
 *
 * Author: Choo Jian Wei
 * Date Created: 30/10/2026
 * Last Modified: 30/10/2026
 *
 * Description:
 *     Part of the Unity client's backend layer. Training requests send the token the backend issued
 *     at login as "Authorization: Bearer <token>". It is kept in memory only, so every launch logs
 *     in again, and it is treated as expired a minute early so a request never arrives just after
 *     it lapses.
 */

using System;

namespace Formula9.Backend
{
    // -----------------------------------------------------------------------------------
    // Class: AuthSession
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class holds the logged-in user and the token the backend issued at login.
    //
    //     Attributes: Username, Token and ExpiresAtUtc describe the login; IsLoggedIn is true while
    //     the token is present and more than ExpiryMargin (60 s) from expiry; Ended is raised when
    //     the backend reports the token expired (401) or the user logs out.
    public sealed class AuthSession
    {
        // Treat the token as expired a minute early, so a request never arrives just after it lapses.
        public static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(60);

        private readonly Func<DateTime> utcNow;

        public string Username { get; private set; }
        public string Token { get; private set; }
        public DateTime ExpiresAtUtc { get; private set; }

        // Raised when the backend reports the token expired (401) or the user logs out.
        public event Action Ended;

        // -----------------------------------------------------------------------------------
        // Function: AuthSession (constructor)
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function creates a logged-out session.
        //
        // Args:
        //     utcNow (Func<DateTime>): The current UTC time; the system clock when null. Tests pass
        //         a fixed clock.
        public AuthSession(Func<DateTime> utcNow = null) => this.utcNow = utcNow ?? (() => DateTime.UtcNow);

        public bool IsLoggedIn => !string.IsNullOrEmpty(Token) && utcNow() < ExpiresAtUtc - ExpiryMargin;

        // -----------------------------------------------------------------------------------
        // Function: Begin
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function records a successful login. The lifetime comes from the backend as a
        //     number of seconds, so a skewed local clock cannot shorten or extend it.
        //
        // Args:
        //     username (string): The account name.
        //     token (string): The bearer token.
        //     expiresInSeconds (long): How long the token lasts, from the login reply.
        //
        // Throws:
        //     ArgumentException: If the reply is missing its user, token or a positive lifetime.
        public void Begin(string username, string token, long expiresInSeconds)
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(token) || expiresInSeconds <= 0)
                throw new ArgumentException("Login reply is missing its user, token or lifetime.");
            Username = username;
            Token = token;
            ExpiresAtUtc = utcNow().AddSeconds(expiresInSeconds);
        }

        // -----------------------------------------------------------------------------------
        // Function: Authorize
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function adds the bearer token to a request while logged in.
        //
        // Args:
        //     request (HttpRequest): The request to authorise.
        public void Authorize(HttpRequest request)
        {
            if (IsLoggedIn) request.Headers["Authorization"] = "Bearer " + Token;
        }

        // -----------------------------------------------------------------------------------
        // Function: Expire
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function ends the login after the backend rejected a token (401), but only if
        //     that was the current token. A 401 for an older token, for example from before a re-
        //     login, must not end the new login.
        //
        // Args:
        //     tokenUsed (string): The token the rejected request carried.
        public void Expire(string tokenUsed)
        {
            if (string.IsNullOrEmpty(Token) || tokenUsed != Token) return;
            End();
        }

        // -----------------------------------------------------------------------------------
        // Function: End
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function forgets the login and raises Ended.
        public void End()
        {
            Username = null;
            Token = null;
            ExpiresAtUtc = default;
            Ended?.Invoke();
        }
    }
}
