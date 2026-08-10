# Iroh.NET

[![NuGet Build](https://github.com/IsaMorphic/N0.IrohNet/actions/workflows/nuget.yml/badge.svg)](https://github.com/IsaMorphic/N0.IrohNet/actions/workflows/nuget.yml) [![NuGet Version](https://img.shields.io/nuget/v/N0.IrohNet?style=flat&label=NuGet%20version)](https://www.nuget.org/packages/N0.IrohNet)

This repository hosts unofficial C# .NET bindings for the [iroh library](https://iroh.computer/) by [N0 Inc.](https://n0.computer/) The project uses [CySharp's csbindgen](https://github.com/CySharp/csbindgen) library to create a shim using [iroh's C FFI](https://github.com/n0-computer/iroh-c-ffi) bindings that can be used directly in .NET code via traditional interop. 

# How to Use

This repository publishes a series of NuGet packages via GitHub Actions. Simply install [N0.IrohNet](https://www.nuget.org/packages/N0.IrohNet) into your project from NuGet to get started! The instructions below detail how to build these bindings locally on your machine. 

## Prerequisites

To build Iroh.NET locally, your development environment needs the following tools installed:

**Required:**

1. [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
2. [Cargo (via rustup)](https://rustup.rs/)
3. [Git](https://git-scm.com/install/)

**Optional (for targeting other platforms):**

Linux

1. [Podman](https://podman.io/docs/installation)
2. [`cargo-cross`](https://github.com/zijiren233/cargo-cross)

Android

1. [Android Studio](https://developer.android.com/studio)
2. [`cargo-ndk`](https://github.com/bbqsrc/cargo-ndk)

iOS / macOS

1. [XCode](https://developer.apple.com/xcode/)

## Adding the Code to your Project

In your own project's Git repository, run the following command:

```bash
git submodule add "https://github.com/IsaMorphic/N0.IrohNet.git" external/N0.IrohNet
```

Next, run this command to download any recursive dependencies:

```bash
git submodule update --init --recursive
```

Once completed, you may add the `.csproj` found in `external/N0.IrohNet` to any of your other MSBuild projects:

```xml
<ProjectReference Include="..\external\N0.IrohNet\N0.IrohNet\N0.IrohNet.csproj" />
```

## Compiling

### For Local Development (Desktop)

To use these bindings locally in a desktop development environment, the native dependencies must be compiled first. Follow the instructions below for the relevant platform. Only one of these commands need be executed for a given platform depending on the architecture of the development machine. 

#### Windows

```powershell
# Build for x64 machines
dotnet build N0.IrohNet.NativeAssets.Win32 --framework net10.0 --runtime win-x64

# Build for ARM64 machines
dotnet build N0.IrohNet.NativeAssets.Win32 --framework net10.0 --runtime win-arm64
```

#### macOS

```bash
# Build for x64 machines
dotnet build N0.IrohNet.NativeAssets.macOS --framework net10.0 --runtime osx-x64

# Build for ARM64 machines
dotnet build N0.IrohNet.NativeAssets.macOS --framework net10.0 --runtime osx-arm64
```

#### Linux

```bash
# Build for x64 machines
dotnet build N0.IrohNet.NativeAssets.Linux --framework net10.0 --runtime linux-x64

# Build for ARM64 machines
dotnet build N0.IrohNet.NativeAssets.Linux --framework net10.0 --runtime linux-arm64
```

### For Mobile Development (Android)

For native Android builds, one must ensure that `cargo-ndk` is installed in a Linux-based development machine. WSL works just fine for Windows users. Then, ensure that Android Studio is installed within that environment and add an older NDK version (r29 works just fine). Then run one of the following commands to compile for the architecture of choice.

```bash
# Build for x64 devices (emulators)
dotnet build N0.IrohNet.NativeAssets.Android --runtime android-x64

# Build for ARMv7 devices (older phones)
dotnet build N0.IrohNet.NativeAssets.Android --runtime android-arm

# Build for ARM64 devices (newer phones)
dotnet build N0.IrohNet.NativeAssets.Android --runtime android-arm64
```

Finally, in the Android application project file, include a reference to the Android assets folder as follows. Doing so will ensure that the native `iroh-bindgen` library is included in the APK.

```xml
<ProjectReference Include="..\external\N0.IrohNet\N0.IrohNet.NativeAssets.Android\N0.IrohNet.NativeAssets.Android.csproj" />
```

### For Mobile Development (iOS)

For native iOS builds, ensure that XCode is installed on your macOS build machine, along with the current version of XCode Command-line Tools and the most recent iOS SDK. Then run one of the following commands to compile for the architecture of choice.

```bash
# Build for x64 machines (simulator)
dotnet build N0.IrohNet.NativeAssets.iOS --runtime iossimulator-x64

# Build for ARM64 machines (simulator)
dotnet build N0.IrohNet.NativeAssets.iOS --runtime iossimulator-arm64

# Build for ARM64 devices (iPhones & iPads)
dotnet build N0.IrohNet.NativeAssets.iOS --runtime ios-arm64
```

Finally, in the iOS application project file, include a reference to the iOS assets folder as follows. Doing so will ensure that the native `iroh-bindgen` library is included in the app bundle.

```xml
<ProjectReference Include="..\external\N0.IrohNet\N0.IrohNet.NativeAssets.iOS\N0.IrohNet.NativeAssets.iOS.csproj" />
```

## Updating the Bindings

To update these bindings within your repository, go to the `external/N0.IrohNet` subdirectory and run:

```bash
git pull
```

Then, run this command in the repository root:

```bash
git submodule update --init --recursive
```

Finally to commit the update to your codebase, run:

```bash
git commit -a -m "Update bindings to latest upstream revision"
```

# Contributing

Feel free to open a PR if something needs to be fixed! Thank you!