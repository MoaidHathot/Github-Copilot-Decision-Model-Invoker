# GitHub Copilot Decision Model Invoker

A single-file .NET CLI and MCP server for trying decision models with your GitHub Copilot account. **For demos, testing, and experimentation; not production use.**

## Prerequisites

- [.NET 11 SDK](https://dotnet.microsoft.com/download/dotnet/11.0).
- A GitHub Copilot account with access to the decision models.
- Sign in using `copilot login` or `gh auth login --web --hostname github.com`.

The first build downloads the matching Copilot runtime. No PAT is needed for saved-login authentication.

## Run

```powershell
# CLI: run the sample decision request.
dotnet run --file Decision.cs -- decide --input examples/ticket.json

# MCP: build once, then start the stdio server.
dotnet build Decision.cs
dotnet run --no-build --file Decision.cs -- mcp
```

The default model is `microsoft-decision-1`. Add `--model gpt-6-luna-decisions` to select Luna, or `--transport http` to call the HTTP API directly.

[Full usage and configuration](docs/USAGE.md) · [MCP configuration example](mcp.example.json) · [Research](RESEARCH.md)
