# Sign in with Google

Players can sign in with their Google account or just type a name and play as a guest. Invite links
work the same either way.

| | Guest | Signed in with Google |
|---|---|---|
| Name | whatever they type | their Google profile name (locked) |
| Player id | random, kept in this browser only | `google-<Google account id>`, the same on every device and browser |
| Rejoin a running game after closing the tab | same browser only | from any device |

## How it works

Sign-in is **Azure Container Apps built-in authentication** (also called *Easy Auth*): a proxy that
Azure runs in front of the app. The app contains no OAuth code and no Google library.

```
Browser ──/.auth/login/google──▶ Container Apps auth proxy ──▶ Google (OpenID Connect)
        ◀── session cookie ────┘
Browser ──GET /api/me──▶ proxy adds X-MS-CLIENT-PRINCIPAL ──▶ app returns name + a signed game token
Browser ──SignalR /hubs/game?access_token=…──▶ app checks the token ──▶ plays as google-<id>
```

- The proxy owns `/.auth/login/google`, `/.auth/login/google/callback`, `/.auth/logout` and `/.auth/me`.
- `unauthenticatedClientAction: AllowAnonymous`: the proxy doesn't force anyone to sign in; it just
  tells the app who is signed in, if anyone.
- The proxy passes the user in `X-MS-CLIENT-PRINCIPAL` (base64 JSON of the claims) and
  `X-MS-CLIENT-PRINCIPAL-ID`. It removes any copy a client tries to send, so the app can trust them,
  but **only** behind the proxy. That's why the app reads them only when `Auth__EasyAuth=true`, which
  Bicep sets only when Google sign-in is configured. Locally the setting is off.
- The game runs over a WebSocket. Rather than rely on the proxy adding headers to WebSocket requests,
  `/api/me` issues a 60-minute token signed with ASP.NET Core Data Protection. SignalR's
  `accessTokenFactory` fetches a fresh one on every connect and reconnect. Guests can't use ids that
  start with `google-`.
- Code: `src/Triviador.Server/Auth/PlayerIdentity.cs`, `/api/me` in `Program.cs`, `loadMe()` and
  `renderAccount()` in `wwwroot/app.js`. Infra: the `authConfigs` resource in `infra/main.bicep`.

## Setting it up (once, about 10 minutes, free)

### 1. Create the Google OAuth client

1. Open https://console.cloud.google.com/ and create a project, e.g. **Triviador**.
2. Go to **APIs & Services → OAuth consent screen** (Google may call it *Google Auth Platform*) and
   click **Get started**:
   - App name **Quiz Cards**, your email as the support email.
   - Audience **External**.
   - Your email as the contact address. Accept and **Create**.
3. **Audience:** while the app is in *Testing*, only the Google accounts listed under **Test users**
   can sign in (up to 100). Add yours and your friends'. To let anyone sign in, click **Publish app**.
   With only the basic `openid`, `email` and `profile` scopes, Google doesn't require a review.
4. **Clients → Create client:**
   - Application type **Web application**, name **Quiz Cards (Azure)**.
   - **Authorized redirect URIs:** `https://<your app address>/.auth/login/google/callback`. This is
     the `googleRedirectUri` output of the deployment, e.g.
     `https://triviador.<something>.swedencentral.azurecontainerapps.io/.auth/login/google/callback`.
   - **Create**, then copy the **Client ID** and **Client secret**.

### 2. Give them to GitHub

Repo → **Settings → Secrets and variables → Actions**:
- **Variables** tab: `GOOGLE_CLIENT_ID` = the client id. It's public anyway; it shows in the login URL.
- **Secrets** tab: `GOOGLE_CLIENT_SECRET` = the client secret. GitHub keeps it encrypted and masks it
  in logs. In Azure it becomes a Container App secret.

### 3. Deploy

Push, or re-run the workflow. When it finishes, the lobby shows **Sign in with Google**.

## Checking it

- `https://<app>/.auth/me` shows the proxy's view of you (`[]` when signed out).
- `https://<app>/api/me` shows the app's view: `signedIn`, `playerId`, `name`.
- Error `redirect_uri_mismatch` on Google's page: the redirect URI in Google Cloud must match exactly
  (https, no trailing slash, `/.auth/login/google/callback`).
- Error *Access blocked* or *not a test user*: add the account under **Test users**, or publish the app.

## Turning it off

Delete the `GOOGLE_CLIENT_ID` variable and redeploy. `Auth__EasyAuth` goes back to `false`, so the app
ignores sign-in headers and the button disappears. Deleting a variable doesn't delete the Azure auth
setting, so also remove it with
`az containerapp auth update -g rg-triviador -n triviador --enabled false`.

## Exam notes (AI-200)

- Built-in authentication works the same way in App Service, Functions and Container Apps: no code,
  OpenID Connect providers (Microsoft Entra, Google, GitHub, Apple and others), and the identity
  reaches your code as `X-MS-CLIENT-PRINCIPAL*` headers.
- *Require authentication* (HTTP 401 or a redirect to login) versus *Allow unauthenticated* (your code
  decides). This app uses the second.
- The client secret is stored as an app secret and referenced by name (`clientSecretSettingName`), so
  it never sits in plain configuration.
- For Microsoft-account and work logins with your own app registration and MSAL, see Microsoft Entra
  External ID (milestone 5 in the design notes).
