<p align="center">
  <img src="https://raw.githubusercontent.com/araafario/seneschal/main/assets/branding/seneschal.png" width="96" alt="Seneschal">
</p>

<h1 align="center">LoopUp Dev Environment</h1>

<p align="center">
  An extension for <a href="https://github.com/araafario/seneschal">Seneschal</a> containing tools for developing for LoopUp: run your stack, follow its logs and get a token, by hand or by asking Claude.
</p>

## What is in it

This extension adds a set of LoopUp development tools to Seneschal, all under **LoopUp** in the sidebar:

| Tool | What it does |
| --- | --- |
| **Projects** | Start, stop and watch Docker containers, .NET projects (even several copies of one) and microfrontends. A Smart Startup brings them up in order and waits for each step. See [Projects and Logs](docs/dev-tools.md) |
| **Logs** | Follow several logs side by side, stacked or in a grid, and filter the lines |
| **LoopUp Token Generator** | Pick Local, Lab or Production, enter your email and password and get a bearer token. Your password is never saved |

Every feature is also a tool for the assistant: ask *What is running right now?*, *Start my morning stack* or *Get me a local token*. Anything that deletes asks first.

## Install

You need [Seneschal](https://github.com/araafario/seneschal) 1.2 or newer.

1. Download `loopup-dev-env.snx` from the [latest release](../../releases/latest)
2. In Seneschal open the **Extension Manager**, press **Import** and choose the file
3. The tools appear under **LoopUp** at once. There is nothing to restart

To remove it, delete it in the Extension Manager. Nothing else in Seneschal depends on it.

If you used the Dev Environment that used to ship with Seneschal, your projects, startups and logs are picked up from `%AppData%/Seneschal/extension-data/dev-environment` the first time this one starts. They are copied, never moved.

## How the token sign in works

All of it with one cookie jar, in `Token/TokenClient.cs`:

1. `GET {identity}/connect/authorize` with the client `oneui`, a PKCE challenge and the redirect `{one}/oidc-callback` answers 302 to `/login?returnUrl=...`
2. `POST {identity}/login-password` with `{ email, password, returnUrl, rememberMe }` sets the session cookie and answers with the address to continue at
3. that address answers 302 to `{one}/oidc-callback?code=...`
4. `POST {identity}/connect/token` trades the code and the PKCE verifier for the tokens

## Build

The project builds against the [`Seneschal.Sdk`](https://www.nuget.org/packages/Seneschal.Sdk) package alone. Nothing from the Seneschal repository is needed.

```powershell
dotnet build Seneschal.LoopupDevEnv -c Release
```

The package's build rules stage the extension and zip it to `Seneschal.LoopupDevEnv/artifacts/packages/loopup-dev-env.snx`.

To try a build without importing: set `SENESCHAL_DEV_EXTENSIONS` to `Seneschal.LoopupDevEnv/artifacts/extensions`, run `seneschal restart` once, and rebuild after each change: every build is loaded by itself and the page you have open updates (Seneschal 1.2 or newer).

## Releasing

Actions, **Release**, Run workflow. It builds the extension, tags the commit `v<version>` (the version in `extension.json`) and puts `loopup-dev-env.snx` on a GitHub Release. Nothing is released on push.
