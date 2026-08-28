# The .NET version has to track <TargetFramework> in Directory.Build.props.
# This image already carries make, git and gcc.
FROM mcr.microsoft.com/devcontainers/dotnet:1-8.0-bookworm

# The image ships an apt source for yarn signed by a key it no longer has: the bundled
# /usr/share/keyrings/yarn-archive-keyring.gpg holds 72EC...86E50310, whose signing subkeys
# have all expired, while dl.yarnpkg.com now signs with 62D54FD4003F6525. `apt-get update`
# therefore fails outright, and every devcontainer feature that installs a package fails
# with it. Nothing in this repo uses yarn - both npm projects ship a package-lock.json - so
# the source just goes. This has to precede every apt-get update, the features' included.
RUN rm -f /etc/apt/sources.list.d/yarn.list

# tmux is baked into the image rather than installed at postCreate: the claude-tmux shim
# needs it, and an image layer is cached, always present, and costs no apt round-trip - nor
# a network flake - on every rebuild. Depends on the yarn source removal above.
RUN apt-get update \
    && export DEBIAN_FRONTEND=noninteractive \
    && apt-get install -y --no-install-recommends tmux \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

# Claude Code. Installed as the user that will actually run it, because the installer is
# per-user: it drops a versioned binary in ~/.local/share/claude and links it from
# ~/.local/bin, which this base image already has on PATH for both login and interactive
# shells. A native binary, so it does not need the Node feature - which is layered on after
# this build anyway and so would not be available here.
USER vscode
RUN curl -fsSL https://claude.ai/install.sh | bash

# Back to root: devcontainer features are layered on top of this image and install as root.
USER root
