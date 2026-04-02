# AstroManager.NinaPlugin

Public repository for the AstroManager N.I.N.A. plugin.

## Contents

- `AstroManager.NinaPlugin`

## Dependency Model

This repository consumes the following shared packages:

- `AstroManager.Contracts`
- `AstroManager.PluginShared`

The packages are expected to be published from the public `AstroManager.NinaShared` repository via GitHub Packages.

## First Setup

1. Update `NuGet.config` and replace `<OWNER>` with your GitHub user or organization.
2. Publish the shared packages from `AstroManager.NinaShared`.
3. Restore and build this repository.
4. Create the plugin release asset.
5. Publish the asset on GitHub Releases.
6. Generate the N.I.N.A. manifest and submit it to the official manifest repository.

## Notes

- License: MIT
- The plugin source repository must remain public for the official N.I.N.A. community manifest repository.
