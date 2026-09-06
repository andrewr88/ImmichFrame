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

- [`Settings.yml` example][example-yaml]
- [`Settings.json` example][example-json]
- [`.env` example][example-env]

---

## Docker Compose Example

:::warning Important
If using yaml or json settings, replace `PATH/TO/CONFIG` with the actual path to your config folder containing the settings file!
:::

:::info Writable config folder
If you want to use the [admin configuration editor](/docs/getting-started/admin-editor), the folder
you mount at `/app/Config` has to be writable by UID 1000 — ImmichFrame runs as that user and writes
your settings file and its backups there. Mounting it read-only is fine otherwise; the editor just
refuses to save.
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