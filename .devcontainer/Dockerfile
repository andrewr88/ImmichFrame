# The .NET version has to track <TargetFramework> in Directory.Build.props.
# This image already carries make, git and gcc, so nothing needs installing here.
FROM mcr.microsoft.com/devcontainers/dotnet:1-8.0-bookworm

# The image ships an apt source for yarn signed by a key it no longer has: the bundled
# /usr/share/keyrings/yarn-archive-keyring.gpg holds 72EC...86E50310, whose signing subkeys
# have all expired, while dl.yarnpkg.com now signs with 62D54FD4003F6525. `apt-get update`
# therefore fails outright, and every devcontainer feature that installs a package fails
# with it. Nothing in this repo uses yarn - both npm projects ship a package-lock.json - so
# the source just goes.
RUN rm -f /etc/apt/sources.list.d/yarn.list
