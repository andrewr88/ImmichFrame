# Devcontainer

An isolated, repeatable dev environment for ImmichFrame. Open the repo in VS Code and pick
**Reopen in Container**, or run `devcontainer up --workspace-folder .` with the devcontainer CLI.

## What's inside

| Tool       | Version | Why                                                          |
| ---------- | ------- | ------------------------------------------------------------ |
| .NET SDK   | 8.0     | `<TargetFramework>` in `Directory.Build.props`                 |
| Node.js    | 22      | matches the frontend build stage in the production `Dockerfile` |
| make       | 4.3     | every documented entry point is a `make` target (in base image) |
| gh         | latest  | fork workflow (syncing `main`, opening PRs into `custom`)      |
| docker CLI | host    | `make docker-prod` / `make docker-build-prod`                  |

Docker is wired to the **host** daemon rather than a nested one, so images you build inside the
container land in your normal `docker images` list.

## First run

`post-create.sh` restores NuGet packages, runs `npm ci` for both `immichFrame.Web` and `docs`, and
seeds `docker/.env` from `docker/example.env` if it does not exist yet.

That seeded file carries placeholders — `ImmichServerUrl=URL` and `ApiKey=KEY`. **The API refuses to
start until you point them at a real Immich server**, because ImmichFrame checks every configured
server's version on startup and exits if one is unreachable. `docker/.env` is already gitignored
(see `docker/.gitignore`), so your API key will not be committed.

Alternatively, set `IMMICHFRAME_CONFIG_PATH` to a directory holding a `Settings.json` or
`Settings.yml` — that takes precedence over environment variables.

## The dev loop

Run the two halves in separate terminals:

```bash
make dev                              # API on http://localhost:5217, Swagger at /swagger
npm --prefix immichFrame.Web run dev  # web on http://localhost:5173
```

Open **5173** — Vite proxies `/api` and `/static` through to 5217, so you get the whole app with
hot reload on the frontend.

Running them separately is deliberate. `make dev` alone tries to start the frontend through
ASP.NET's SpaProxy, which is configured to wait for `https://localhost:5173` while Vite serves
plain HTTP; the API still comes up fine, but the proxy sits there polling. Two terminals sidesteps
it entirely.

## Other targets

```bash
make test-core      # ImmichFrame.Core.Tests
make test-webapi    # ImmichFrame.WebApi.Tests
make docs           # docs site on http://localhost:3000
make api            # regenerate the TS API client — needs `make dev` running first
```

`make api` curls `http://localhost:5217/swagger/v1/swagger.json` into `openApi/`, then regenerates
`immichFrame.Web/src/lib/immichFrameApi.ts` from it. Both steps need the API up.

## Notes

- Ports 5217, 5173 and 3000 are forwarded automatically.
- The `Dockerfile` exists for one reason: the base image ships an apt source for yarn signed by a
  key it no longer carries, which makes `apt-get update` fail and takes every package-installing
  feature down with it. Removing that source has to happen in a Dockerfile, because features are
  layered on top of the built image. Everything else the container needs is either already in the
  base image (.NET 8, make, git, gcc) or arrives as a feature.
