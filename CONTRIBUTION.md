# Contributing to Tubifarry

Thank you for your interest in contributing to Tubifarry! This guide
will help you set up your development environment and understand our
contribution process. 
Tubifarry is a plugin for Lidarr that extends its functionality.


## Table of Contents

- [Prerequisites](#prerequisites)
- [Understanding the Architecture](#understanding-the-architecture)
- [Automatic Setup (Recommended)](#automatic-setup-recommended)
- [Build Automation Explained](#build-automation-explained)
- [Manual Setup (Fallback)](#manual-setup-fallback)
- [Development Workflow](#development-workflow)
- [Contributing Code](#contributing-code)
- [Pull Request Guidelines](#pull-request-guidelines)
- [Troubleshooting](#troubleshooting)
- [Getting Help](#getting-help)


## Prerequisites

Before you begin, ensure you have the following installed on your system:

### Required Software

- **Visual Studio 2022** or higher (Community edition is free)
  - Download: https://visualstudio.com/downloads/
  - Must include .NET 8 SDK (VS 2022 V17.0+)
- **Git** - Version control system
- **Node.js 20.x** - JavaScript runtime for frontend development
  - Download: https://nodejs.org/
  - ⚠️ **Important**: Versions 18.x, 16.x, or 21.x will NOT work
- **Yarn** - Package manager for Node.js dependencies
  - Included with Node 20+ (enable with `corepack enable`)
  - For other versions: `npm i -g corepack`

### Optional but Recommended

- **Rider** by JetBrains (alternative to Visual Studio)
- **VS Code** or similar text editor for quick edits

### System Requirements

- **Windows**: Windows 10/11
- **Linux**: Any modern distribution
- **macOS**: 10.14+ (Mojave or newer)
- **RAM**: Minimum 8GB (16GB recommended)
- **Storage**: At least 5GB free space for the full build

## Understanding the Architecture

Tubifarry is built as a plugin that integrates with Lidarr's core functionality:

- **Lidarr Core**: The base music management application (included as a Git submodule)
- **Tubifarry Plugin**: Your plugin code that extends Lidarr
- **Frontend**: React-based UI (part of Lidarr)
- **Backend**: C# .NET 8 code (both Lidarr and Tubifarry)

### Important Notes About the Build Process

1. **Lidarr Submodule**: Tubifarry includes Lidarr as a Git submodule, which means it contains a reference to a specific version of Lidarr
2. **Force Push Warning**: The Lidarr develop branch is regularly rebased and force-pushed. This is normal and expected behavior
3. **Build Order Matters**: You must build Lidarr completely before building Tubifarry
4. **First Build Takes Time**: The initial setup can take 5-10 minutes, but subsequent builds are much faster


## Automatic Setup (Recommended)

The project includes build automation that handles most of the setup process automatically. This is the fastest way to get started.

### Step 1: Clone the Repository

```bash
git clone https://github.com/TypNull/Tubifarry.git
```

Or if you've forked the repository:
```bash
git clone https://github.com/YOUR-USERNAME/Tubifarry.git
```

### Step 2: Open and Build

1. **Open** `Tubifarry.sln` in Visual Studio

2. **Build the solution** by pressing `Ctrl + Shift + B` (or `Build` → `Build Solution`)
   
   ⏳ **Wait patiently** - This first build will take some time depending on your internet connection and PC performance. The build automation will:
   - Initialize and clone all Git submodules (Lidarr source code)
   - Install Node.js dependencies via Yarn
   - Build the Lidarr frontend
   
   You can monitor progress in the **Output** window (`View` → `Output`).

### Step 3: Restart Visual Studio

After the first build completes:

1. **Close** Visual Studio completely
2. **Reopen** Visual Studio
3. **Open** `Tubifarry.sln` again

This ensures Visual Studio properly recognizes all the newly generated files and references.

### Step 4: Build Again

1. **Build the solution** again (`Ctrl + Shift + B`)
   
   ⚠️ You may see some errors on this build - **these can be ignored**.

2. **Build once more** (`Ctrl + Shift + B`)
   
   The build should now complete successfully.

### Step 5: Configure Startup Project

1. In **Solution Explorer**, navigate to: `Submodules` → `Lidarr` → find `Lidarr.Console`
2. **Right-click** on `Lidarr.Console`
3. Select **"Set as Startup Project"**

### Step 6: Run

Press `F5` or click the **Start** button to run Lidarr.

---

**That's it!** If everything worked, you're ready to start developing. If you encountered errors, see the [Manual Setup](#manual-setup-fallback) or [Troubleshooting](#troubleshooting) sections below.


## Build Automation Explained

The project uses several MSBuild `.targets` files to automate the setup and build process. Understanding these can help with troubleshooting.

### PreBuild.targets - Automatic Initialization

This file handles automatic setup before the main build starts:

**Submodule Initialization (`InitializeSubmodules` target)**
- Checks if the Lidarr submodule exists by looking for `Submodules/Lidarr/src/NzbDrone.Core/Lidarr.Core.csproj`
- If missing, automatically runs `git submodule update --init --recursive`

**Frontend Build (`LidarrFrontendBuild` target)**
- Checks if the Lidarr frontend has been built by looking for `Submodules/Lidarr/_output/UI/`
- If missing, automatically runs:
  - `yarn install` - Downloads all Node.js dependencies
  - `yarn build` - Compiles the React frontend
- This can take several minutes on first run

### ILRepack.targets - Assembly Merging

After the build completes, this file merges all plugin dependencies into a single DLL:

### Debug.targets - Automatic Plugin Deployment

In Debug configuration, this file automatically deploys your plugin:

- After ILRepack finishes, copies the plugin files to Lidarr's plugin directory:
  - `C:\ProgramData\Lidarr\plugins\AUTHOR\Tubifarry\`
- Allows immediate testing without manual file copying

### Build Order

The targets execute in this order:
1. `InitializeSubmodules` → Ensures Lidarr source exists
2. `LidarrFrontendBuild` → Ensures frontend is compiled
3. `BeforeBuild` → Standard .NET build preparation
4. `Build` → Compiles Tubifarry code
5. `CopySystemTextJson` → Copies required runtime DLL
6. `ILRepacker` → Merges all assemblies
7. `PostBuild` (Debug only) → Deploys to plugin folder


## Manual Setup (Fallback)

If the automatic setup fails or you prefer manual control, follow these steps.

### Step 1: Fork and Clone

1. **Fork the Repository**
   - Go to https://github.com/TypNull/Tubifarry
   - Click the "Fork" button in the top right
   - This creates your own copy of the repository

2. **Clone Your Fork**
   ```bash
   git clone https://github.com/YOUR-USERNAME/Tubifarry.git
   cd Tubifarry
   ```

### Step 2: Initialize Git Submodules

The Tubifarry repository includes Lidarr as a submodule. You need to initialize and download it:

```bash
git submodule update --init --recursive
```

This command downloads the Lidarr source code into the submodule directory. This may take a few minutes depending on your internet connection.

### Step 3: Verify Submodule

Check that the Lidarr submodule was properly initialized:

```bash
cd Submodules/Lidarr/
git status
```

You should see the Lidarr repository files.

### Step 4: Build Lidarr Frontend

The frontend must be built before proceeding to the backend.

1. **Navigate to the Lidarr submodule directory**
   ```bash
   cd Submodules/Lidarr/
   ```

2. **Install Node dependencies**
   ```bash
   yarn install
   ```
   This downloads all required JavaScript packages. First time takes 2-5 minutes.

3. **Build the frontend** (or start the watcher for development)
   ```bash
   yarn build
   ```
   
   Or for active development with hot-reload:
   ```bash
   yarn start
   ```
   **Important**: If using `yarn start`, keep this terminal window open.

### Step 5: Build Lidarr Backend

1. **Open the Lidarr solution in Visual Studio**
   - Navigate to the Lidarr submodule directory
   - Open `Lidarr.sln` in Visual Studio 2022

2. **Configure the startup project**
   - Right-click on `Lidarr.Console` in Solution Explorer
   - Select "Set as Startup Project"

3. **Build the solution**
   - Click `Build` → `Build Solution` (or press `Ctrl+Shift+B`)
   - Wait for the build to complete (first build takes a bit time)
   - Watch the Output window for any errors

### Step 6: Build Tubifarry Plugin

Now that Lidarr is fully built, you can build the Tubifarry plugin.

1. **Navigate back to the Tubifarry root directory**
   ```bash
   cd [path-to-Tubifarry-root]
   ```

2. **Open the Tubifarry solution**
   - Open `Tubifarry.sln` in Visual Studio (in a new instance or after closing Lidarr solution)

3. **Wait for dependencies to load**
   - Visual Studio will restore NuGet packages automatically
   - Wait until the status bar shows "Ready"

4. **Build the Tubifarry solution**
   - Click `Build` → `Build Solution`
   - The plugin will automatically copy to the correct directory on Windows in Debug mode

### Step 7: Manual Plugin Deployment (if needed)

If the automatic deployment doesn't work, manually copy the plugin:

1. Find the built plugin at: `Tubifarry/bin/Debug/net8.0/Lidarr.Plugin.Tubifarry.dll`
2. Copy to: `C:\ProgramData\Lidarr\plugins\AUTHOR\Tubifarry\`
3. Also copy the `.pdb` and `.deps.json` files if they exist


## Development Workflow

### Daily Development Process

Once you've completed the initial setup, your typical workflow will be:

1. **Make your changes** to Tubifarry code in Visual Studio

2. **Build Tubifarry** to test your changes (`Ctrl + Shift + B`)

3. **Run and test**
   - Ensure `Lidarr.Console` is set as the startup project
   - Press `F5` to start debugging
   - Lidarr will start with your plugin loaded
   - Access the UI at http://localhost:8686

### Hot Reload (Optional)

For faster frontend iteration:
1. Keep `yarn start` running in the Lidarr submodule directory
2. Frontend changes will automatically refresh in the browser


## Contributing Code

### Before You Start

- **Check existing issues**: Look at [GitHub Issues](https://github.com/TypNull/Tubifarry/issues) to see if your feature/bug is already being worked on
- **Create an issue**: If your idea isn't already tracked, create a new issue to discuss it
- **Ask questions**: Join our Discord or comment on the issue if you need clarification

### Code Guidelines

1. **Code Style**
   - Use 4 spaces for indentation (not tabs)
   - Follow C# naming conventions
   - Keep methods focused and reasonably sized
   - Add XML documentation comments for public APIs

2. **Commit Guidelines**
   - Make meaningful commits with clear messages
   - Use \*nix line endings (LF, not CRLF)
   - Commit message format:
     - `New: Add Spotify playlist import feature`
     - `Fixed: YouTube download timeout issue`
     - `Improved: Error handling in Slskd client`

3. **Code Quality**
   - Test your changes thoroughly
   - Handle errors gracefully
   - Add logging for debugging purposes
   - Don't leave commented-out code

### Branching Strategy

1. **Create a feature branch** from `develop` (not from `master`)
   ```bash
   git checkout develop
   git pull origin develop
   git checkout -b feature/your-feature-name
   ```

2. **Use descriptive branch names**
   - ✅ Good: `feature/spotify-auth`, `fix/youtube-timeout`, `improve/error-messages`
   - ❌ Bad: `patch`, `updates`, `my-branch`

3. **Keep your branch updated**
   ```bash
   git checkout develop
   git pull origin develop
   git checkout feature/your-feature-name
   git rebase develop
   ```

## Pull Request Guidelines

### Before Submitting

- [ ] Code builds without errors
- [ ] You've tested the changes locally
- [ ] Code follows the style guidelines
- [ ] Commits are clean and well-organized (consider squashing if needed)
- [ ] Branch is up to date with `develop`

### Creating a Pull Request

1. **Push your branch** to your fork
   ```bash
   git push origin feature/your-feature-name
   ```

2. **Open a Pull Request** on GitHub
   - Go to your fork on GitHub
   - Click "Compare & pull request"
   - Target the `develop` branch (not `master`)

3. **Fill out the PR template**
   - Describe what your PR does
   - Reference any related issues (e.g., "Fixes #123")
   - Add screenshots if it's a UI change
   - List any breaking changes

4. **Respond to feedback**
   - Maintainers may request changes
   - Make the requested updates in your branch
   - Push the changes (they'll automatically update the PR)

### PR Review Process

- We aim to review PRs within a few days
- If it's been longer, feel free to ping us on Discord
- Be patient and respectful during code review
- All PRs require approval before merging

### Important Rules

- ⛔ **Never** make PRs to `master` - they will be closed
- ⛔ **Don't** merge `develop` into your feature branch - use rebase instead
- ✅ **Do** create one PR per feature/bugfix
- ✅ **Do** ask questions if you're unsure

## Troubleshooting

### Common Build Errors

#### "Could not load file or assembly 'Lidarr.Core'"

**Cause**: Lidarr backend wasn't fully built before building Tubifarry.

**Solution**:
1. Ensure Lidarr frontend is built (`Submodules/Lidarr/_output/UI/` exists)
2. Open and build `Lidarr.sln` first
3. Then build Tubifarry

#### "Node version not supported" or "Yarn command not found"

**Cause**: Wrong Node.js version or Yarn not enabled.

**Solution**:
```bash
# Check Node version
node --version  # Should be 20.x

# Enable Yarn
corepack enable

# Verify Yarn is available
yarn --version
```

#### Frontend changes not appearing

**Cause**: Frontend not rebuilt or browser cache.

**Solution**:
1. Navigate to Lidarr submodule directory
2. Run `yarn build` (or `yarn start` for development)
3. Refresh your browser with `Ctrl+F5` (hard refresh)

#### "Submodule not initialized" or empty Lidarr directory

**Cause**: Git submodules weren't initialized.

**Solution**:
```bash
git submodule update --init --recursive
```

#### Build succeeds but plugin doesn't appear in Lidarr

**Cause**: Plugin DLL not copied to correct location.

**Solution**:
1. Check if you're building in **Debug** configuration (automatic copy only works in Debug)
2. Check your build output directory: `Tubifarry/bin/Debug/net8.0/`
3. Manually copy plugin files to: `C:\ProgramData\Lidarr\plugins\AUTHOR\Tubifarry\`
4. Restart Lidarr
5. Check Lidarr logs for plugin loading errors

#### ILRepack errors

**Cause**: Missing or incompatible assemblies.

**Solution**:
1. Clean the solution (`Build` → `Clean Solution`)
2. Delete the `bin` and `obj` folders in the Tubifarry project
3. Rebuild

### Performance Issues

#### First build is very slow

This is normal! The first build includes:
- Downloading all NuGet packages
- Downloading all Node packages
- Building the frontend (webpack compilation)
- Building everything from scratch

Subsequent builds will be much faster (usually under 1 minute).

#### Visual Studio is slow or freezes

**Solutions**:
- Close unnecessary programs to free up RAM
- Disable unnecessary VS extensions
- Clear VS cache: `Tools` → `Options` → `Projects and Solutions` → `Build and Run` → check "Only build startup projects"
- Consider using Rider instead (lighter weight)

### Getting More Help

If you're still stuck:

1. **Check existing issues**: https://github.com/TypNull/Tubifarry/issues
2. **Search Discord**: Past questions may have been answered
3. **Ask on Discord**: Include:
   - What you're trying to do
   - What error you're seeing
   - What you've already tried
   - Your OS and software versions

## Getting Help

### Support Channels

- **GitHub Issues**: For bug reports and feature requests
- **Servarr Discord**: For development questions and discussions

### Tips for Getting Help

When asking for help, please include:

1. **What you're trying to accomplish**
2. **What error you're encountering** (exact error message)
3. **What you've already tried**
4. **Your environment**:
   - Operating System
   - Visual Studio version
   - .NET SDK version (`dotnet --version`)
   - Node.js version (`node --version`)

---

## Quick Reference

### First-Time Setup Checklist

- [ ] Install Visual Studio 2022 with .NET 8 SDK
- [ ] Install Git
- [ ] Install Node.js 20.x
- [ ] Enable Yarn (`corepack enable`)
- [ ] Fork Tubifarry repository
- [ ] Clone your fork
- [ ] Initialize submodules (`git submodule update --init --recursive`)
- [ ] Build Lidarr frontend (`cd Submodules/Lidarr && yarn install && yarn build`)
- [ ] Build Lidarr backend (open `Lidarr.sln`, build)
- [ ] Build Tubifarry plugin (open `Tubifarry.sln`, build)
- [ ] Test by running Lidarr.Console

### Before Submitting a PR

- [ ] Code builds successfully
- [ ] Changes tested locally
- [ ] Commits are clean with good messages
- [ ] Branch rebased with latest `develop`
- [ ] PR targets `develop` (not `master`)
- [ ] PR description is complete

---

Thank you for contributing to Tubifarry! Your contributions help make
music management better for everyone.

