# Copilot.Decision.CLI

**`gh-decide`** is a .NET CLI and MCP server for trying decision models with your GitHub Copilot account. **For demos, testing, and experimentation; not production use.**

## Prerequisites

- [.NET 11 SDK](https://dotnet.microsoft.com/download/dotnet/11.0).
- A GitHub Copilot account with access to the decision models.
- Sign in using `copilot login` or `gh auth login --web --hostname github.com`.

The first build downloads the matching Copilot runtime. No PAT is needed for saved-login authentication.

## Run from source

```powershell
# CLI: run the sample decision request.
dotnet run --file Decision.cs -- decide --input examples/ticket.json

# MCP: build once, then start the stdio server.
dotnet build Decision.cs
dotnet run --no-build --file Decision.cs -- mcp
```

The default model is `microsoft-decision-1`. Add `--model gpt-6-luna-decisions` to select Luna, or `--transport http` for direct HTTP.

## NuGet tool

Package ID: **`Copilot.Decision.CLI`**. Installed command: **`gh-decide`**.

```powershell
# Once published to NuGet:
dotnet tool install --global Copilot.Decision.CLI --prerelease
gh-decide models
gh-decide mcp
```

[Usage](https://github.com/MoaidHathot/Github-Copilot-Decision-Model-Invoker/blob/main/docs/USAGE.md) · [Packaging/local install](https://github.com/MoaidHathot/Github-Copilot-Decision-Model-Invoker/blob/main/docs/PACKAGING.md) · [MCP configuration](https://github.com/MoaidHathot/Github-Copilot-Decision-Model-Invoker/blob/main/mcp.example.json)
