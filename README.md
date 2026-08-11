# Single Player Tarkov - Server Project

This is the Server project for the Single Player Tarkov mod for Escape From Tarkov. It can be run locally to replicate responses to the modified Escape From Tarkov client.


# Table of Contents

- [Installation](#installation)
  - [Requirements](#requirements)
  - [Initial Setup](#initial-setup)
- [Development](#development)
  - [Commands](#commands)
  - [Debugging](#debugging)
  - [Mod Debugging](#mod-debugging)
- [Deployment](#deployment)
  - [Docker](#docker)
- [Contributing](#contributing)
  - [Branches](#branches)
  - [Pull Request Guidelines](#pull-request-guidelines)
  - [Style Guide](#style-guide)
  - [Tests](#tests)
- [License](#license)

## Installation

### Requirements

This project has been built in [Visual Studio](https://visualstudio.microsoft.com/) (VS) and [Rider](https://www.jetbrains.com/rider/) using [.NET](https://dotnet.microsoft.com/en-us/)

One of the following is required:
- Minimum Visual Studio version required: `17.13.5`
- Minimum Rider version required: `2024.3`

Also required:
- Rust toolchain via [rustup](https://rustup.rs) — the build runs `cargo` automatically for the native library (`rust/`, pinned to 1.97.1 by `rust-toolchain.toml`).

### Initial Setup

1. Download and install the [.net 10.0 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).
2. Run `git clone https://github.com/smashedbanan/tarky-server-csharp.git server` to clone the repository
3. Run `scripts/decompress-assets.sh` (or `scripts/decompress-assets.ps1` on Windows) to unpack the bundled
   database files. This requires `7z` on your `PATH`. Unlike upstream, this fork ships those files as a plain
   7z archive rather than over Git LFS, so no `git lfs` setup is needed.
4. Open the `server-csharp.slnx` file in Visual Studio or Rider
5. Run `Build > Build Solution (CTRL + SHIFT + B)` in the IDE

## Development

### Commands

```bash
scripts/decompress-assets.sh   # required before the first build (.ps1 on Windows)
dotnet build                   # builds server-csharp.slnx; also runs cargo for rust/spt-native
dotnet test                    # NUnit suite in Testing/UnitTests
csharpier format .             # apply formatting before opening a PR
cd rust && cargo test && cargo fmt --check && cargo clippy --all-targets -- -D warnings
```

`cargo` must be on your `PATH` for **any** build — `SPTarkov.Server.Core` compiles `rust/spt-native` first, and a
missing Rust toolchain fails the build.

### Debugging

The launch profiles live in `SPTarkov.Server/Properties/launchSettings.json`. Both set the working directory to the
build output, which the server requires — it refuses to start unless `sptLogger.json` is in the current directory,
so running it from the repository root will not work.

To debug the project in Visual Studio:
1. Choose the `Spt Server` profile in the debug drop-down (`Spt Server (Linux)` on Linux)
2. Choose `Debug > Start Debugging (F5)` to run the server

And in Rider:
1. Choose the `Spt Server` run configuration (`Spt Server (Linux)` on Linux)
2. Press `(Alt + F5)` to start debugging

### Mod Debugging

To debug a server mod in Visual Studio, you can copy the mod DLL into the `user/mods` folder and then start the server.

## Deployment

To build the project via CLI:
1. Open the terminal at the project root
2. Run the command `dotnet publish`
    - `-c Release` for release build
    - `-p:SptVersion=*.*.*` to set the version ProgramStatics uses
    - `-p:SptCommit=******` to set the commit ProgramStatics uses
    - `-p:SptBuildTime=*********` to set the buildTime ProgramStatic uses
    - `-p:SptBuildType=*********` to set the BuildType ProgramStatic uses
    - Options for `SptBuildType`: `LOCAL`, `DEBUG`, `RELEASE`, `BLEEDINGEDGE`, `BLEEDINGEDGEMODS` (must be all caps,
      no underscores — these map onto the `EntryType` enum)

Defaults for all four properties live in `Build.props`. Publishing for a runtime other than the build host's also
needs `-p:SptNativeRid=<rid>`, so the native Rust library is cross-compiled to match; only `linux-x64` is mapped.

### Docker

A Linux container image is published to GHCR. See [docker/README.md](docker/README.md) for tags, environment
variables, and persistent-volume layout, and `compose.yaml` in the repository root for an example Compose file.

## Contributing

We're really excited that you're interested in contributing! Before submitting your contribution, please consider the following:

### Branches

- **main**
  The default branch used for the latest stable release. This branch is protected and typically is only merged with release branches.
- **4.1.x-dev-rust**
  The active development branch for this fork, carrying the native Rust work. **PRs should target this.**
- **4.1.x-dev**
  The 4.1 branch tracking upstream server development.
- **4.0.13-legacy**
  The 4.0.13-legacy branch is an archive. **PRs will be denied.**
- **fork-original**
  A snapshot of the upstream tree at the point this fork diverged. Reference only.

### Pull Request Guidelines

- **Keep Them Small**
  If you're fixing a bug, try to keep the changes to the bug fix only. If you're adding a feature, try to keep the changes to the feature only. This will make it easier to review and merge your changes.
- **Perform a Self-Review**
  Before submitting your changes, review your own code. This will help you catch any mistakes you may have made.
- **Remove Noise**
  Remove any unnecessary changes to white space, code style formatting, or some text change that has no impact related to the intention of the PR.
- **Create a Meaningful Title**
  When creating a PR, make sure the title is meaningful and describes the changes you've made.
- **Write Detailed Commit Messages**
  Bring out your table manners, speak the Queen's English and be on your best behaviour.

### Style Guide

We use [CSharpier](https://csharpier.com/) to keep the project's code styled/formatted. You can install it globally by running: `dotnet tool install -g csharpier`. You may then apply the formatting rules by running: `csharpier format .`. Please ensure this is ran before your PR is created to make merges easier. This fork has no CI, so nothing enforces formatting on push — running it locally is on you.

#### Format On Save

There are Plugins for both [VS](https://marketplace.visualstudio.com/items?itemName=csharpier.csharpier-vscode) and [Rider](https://plugins.jetbrains.com/plugin/18243-csharpier) which allow you to automatically format project code when a file is saved.

In Rider, after installing the CSharpier plugin:
- Open `Settings`
- Browse to `Editor`, `Code Style`:
    - Set scheme to `Project`
    - Check `Enable EditorConfig Support`
- Browse to `Tools`, `CSharpier`:
    - Enable "Run On Save"
- Browse to `Tools`, `Actions on save`:
    - Check `Reformat and Cleanup Code`
    - Set to `Reformat & Apply Syntax Style`, `Changed lines`

### Tests

The NUnit suite lives in `Testing/UnitTests`:

```bash
dotnet test                                                       # everything
dotnet test --filter "FullyQualifiedName~MongoIdTests"            # one fixture
dotnet test --filter "Name=EveryRegisteredServiceCanBeResolved"   # one test
```

`DependencyInjectionValidationTests` rebuilds the real service container with mods both on and off, so a broken
`[Injectable]` registration fails the test run rather than a server launch. The Rust crate has its own tests — see
the [Commands](#commands) section.

## License

This project is licensed under the Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International Open Source License. See the [LICENSE](LICENSE) file for details.
