using System;

namespace Formula9.Backend
{
    // The logged-in user and the token the backend issued at login. Training requests send it as
    // "Authorization: Bearer <token>". Kept in memory only, so every launch logs in again.
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

        public AuthSession(Func<DateTime> utcNow = null) => this.utcNow = utcNow ?? (() => DateTime.UtcNow);

        public bool IsLoggedIn => !string.IsNullOrEmpty(Token) && utcNow() < ExpiresAtUtc - ExpiryMargin;

        // expiresInSeconds comes from the backend so a skewed local clock cannot shorten or extend it.
        public void Begin(string username, string token, long expiresInSeconds)
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(token) || expiresInSeconds <= 0)
                throw new ArgumentException("Login reply is missing its user, token or lifetime.");
            Username = username;
            Token = token;
            ExpiresAtUtc = utcNow().AddSeconds(expiresInSeconds);
        }

        public void Authorize(HttpRequest request)
        {
            if (IsLoggedIn) request.Headers["Authorization"] = "Bearer " + Token;
        }

        // A 401 for an older token (for example from before a re-login) must not end the new login.
        public void Expire(string tokenUsed)
        {
            if (string.IsNullOrEmpty(Token) || tokenUsed != Token) return;
            End();
        }

        public void End()
        {
            Username = null;
            Token = null;
            ExpiresAtUtc = default;
            Ended?.Invoke();
        }
    }
}
