---
sidebar_position: 2
---

# 🐋 Docker Setup [Docker Compose]

This guide shows how to start **ImmichFrame** using Docker Compose.

---

## Configuration Files

:::tip Recommended
For most users, the `Settings.yml` setup is easier to read and modify.
:::

Example configuration files:

- [`Settings.yml` example][example-yaml] — imported once on first start
- [`Settings.json` example][example-json] — imported once on first start
- [`.env` example][example-env] — config path, log level, time zone and the admin UI's sign-in settings

Starting without a settings file is fine: [turn the admin UI on](../admin-editor.md#turning-it-on)
by connecting it to your identity provider, then open it at `/admin` and configure everything
there.

---

## Docker Compose Example

:::warning Important
If using yaml or json settings, replace `PATH/TO/CONFIG` with the actual path to your config folder containing the settings file!
:::

:::info Writable config folder
The folder you mount at `/app/Config` has to be writable by UID 1000 — ImmichFrame runs as that
user and keeps its settings database there, along with a `keys` folder holding the keys that sign
[admin UI](../admin-ui.md) sessions. A read-only mount does not work: the container stops at
startup with `Cannot open the settings database in '/app/Config'`.
:::

```yaml
name: immichframe
services:
  immichframe:
    container_name: immichframe
    image: ghcr.io/immichframe/immichframe:latest
    restart: on-failure
    volumes:
      - PATH/TO/CONFIG:/app/Config
    ports:
      - "8080:8080"
    environment:
      TZ: "Europe/Berlin"
```

[github-root]: https://github.com/immichframe/ImmichFrame/blob/main
[example-json]: https://github.com/immichframe/ImmichFrame/blob/main/docker/Settings.example.json
[example-yaml]: https://github.com/immichframe/ImmichFrame/blob/main/docker/Settings.example.yml
[example-env]: https://github.com/immichframe/ImmichFrame/blob/main/docker/example.env