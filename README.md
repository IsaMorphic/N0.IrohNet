# Iroh.NET

This repository hosts unofficial C# .NET bindings for the [Iroh library](https://iroh.computer/) by [N0 Inc.](https://n0.computer/) The project uses [CySharp's csbindgen](https://github.com/CySharp/csbindgen) library to create a shim using Iroh's C FFI bindings that can be used directly in .NET code via traditional interop. 

# How to Use

At the moment, this project does not publish any NuGet packages and must be compiled locally as a dependency to your project. To make this easier, users can add this repository as a submodule for use directly as a project in a solution. 

## Prerequisites

To build Iroh.NET locally, your development environment needs the following tools installed:

**Required:**

1. [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
2. [Cargo (via rustup)](https://rustup.rs/)
3. [Git](https://git-scm.com/install/)

**Optional (for targeting other platforms):**

Android

1. [Android Studio](https://developer.android.com/studio)
2. [`cargo-ndk`](https://github.com/bbqsrc/cargo-ndk)

iOS / macOS

1. [XCode](https://developer.apple.com/xcode/) (required for either)
2. [`cargo-lipo`](https://github.com/timnn/cargo-lipo) (only needed for iOS)

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
<ProjectReference Include="..\external\N0.IrohNet\N0.IrohNet.csproj" />
```

## Compiling

### For Local Development (Desktop)

These bindings will automatically compile the necessary native dependencies using `cargo` on all desktop platforms, namely Windows, macOS, and Linux. Simply invoke `dotnet build` for any dependent projects, making sure to pass an RID either in your project file, or at the command-line. The RID is only necessary to include when publishing or debugging your project. Otherwise, native dependency builds are skipped if left unspecified. 

### For Mobile Development (Android)

For native Android builds, one must ensure that `cargo-ndk` is installed in a Linux-based development machine. WSL works just fine for Windows users. Then, ensure that Android Studio is installed within that environment and add an older NDK version (r29 works just fine). Afterwards, make sure to use `rustup` to add the appropriate target architectures for Android platforms. Then simply pass an Android RID to `dotnet build`!

### For Mobile Development (iOS)

For native iOS builds, ensure that XCode is installed on your macOS build machine, along with the current version of XCode Command-line Tools and the most recent iOS SDK. Next, install `cargo-lipo` from above, and use `rustup` to add iOS targets to `rustc`. Then simply pass an `ios` or `iossimulator` RID to `dotnet build`!

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