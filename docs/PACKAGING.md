# NuGet packaging

- **Package ID:** `Copilot.Decision.CLI`
- **Installed command:** `gh-decide`
- **Initial version:** `0.1.0-alpha.1`
- **Package type:** .NET tool for demos, testing, and experimentation.

The application remains a file-based .NET 11 / C# 15 program. Package properties are in `Decision.cs`; `Directory.Build.targets` includes the README and existing Unlicense in the packages.

## Build and install locally

Run from the repository root with the .NET 11 SDK:

```powershell
dotnet pack Decision.cs -c Release --artifacts-path artifacts/build -o artifacts/packages
dotnet tool install --tool-path artifacts/tools Copilot.Decision.CLI --version 0.1.0-alpha.1 --add-source artifacts/packages

.\artifacts\tools\gh-decide --version
.\artifacts\tools\gh-decide models
.\artifacts\tools\gh-decide decide --text "Please refund the duplicate charge." --question "Does the customer request a refund?"
.\artifacts\tools\gh-decide mcp
```

On Linux/macOS, invoke `./artifacts/tools/gh-decide` instead. For global installation, replace `--tool-path artifacts/tools` with `--global`.

## Platform-specific runtime packages

The Copilot SDK includes native runtime assets. Packing therefore creates a small **`Copilot.Decision.CLI`** dispatcher package and a matching **`Copilot.Decision.CLI.<rid>`** payload package. The .NET tool installer selects the payload for its platform.

By default, `dotnet pack` targets the build machine's portable runtime identifier. On Windows x64, the output is:

```text
Copilot.Decision.CLI.0.1.0-alpha.1.nupkg
Copilot.Decision.CLI.win-x64.0.1.0-alpha.1.nupkg
```

To prepare additional platforms in the same release, set the runtime list before packing:

```powershell
$env:GH_DECIDE_PACKAGE_RIDS = 'win-x64;win-arm64;linux-x64;linux-arm64;osx-x64;osx-arm64'
dotnet pack Decision.cs -c Release --artifacts-path artifacts/build -o artifacts/packages
Remove-Item Env:GH_DECIDE_PACKAGE_RIDS
```

Use the same runtime list and version for the dispatcher and its payloads. The artifacts layout gives each runtime its own build/publish directory. The Windows x64 package was installed and verified locally, including SDK-backed MCP model discovery; other platforms have not been verified here. Do not pack with `CopilotSkipCliDownload=true`: published SDK-mode tools need their matching runtime assets.

## Publish

Publish all RID payloads **before** the dispatcher, with the same version. Obtain a NuGet API key for the package IDs through your usual secret-management setup and expose it as `NUGET_API_KEY` to these commands:

```powershell
$version = '0.1.0-alpha.1'
Get-ChildItem "artifacts/packages/Copilot.Decision.CLI.*.$version.nupkg" | ForEach-Object {
    dotnet nuget push $_.FullName --source https://api.nuget.org/v3/index.json --api-key $env:NUGET_API_KEY
    if ($LASTEXITCODE -ne 0) { throw 'Payload publication failed.' }
}
dotnet nuget push "artifacts/packages/Copilot.Decision.CLI.$version.nupkg" --source https://api.nuget.org/v3/index.json --api-key $env:NUGET_API_KEY
```

After publication:

```powershell
dotnet tool install --global Copilot.Decision.CLI --prerelease
gh-decide --help
gh-decide mcp
```

For subsequent prereleases, change the `Version` directive or use `dotnet pack Decision.cs -p:Version=0.1.0-alpha.2`. The CLI/MCP version is read from assembly metadata, so it follows the package build version.
