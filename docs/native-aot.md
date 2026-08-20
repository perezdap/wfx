# Native AOT

`Wfx.Cli` sets `PublishAot`, `SelfContained`, invariant globalization, size optimization, and Windows x64/ARM64 runtime identifiers in the project. Library projects set `IsAotCompatible` so trimming, single-file, and AOT analyzers run during ordinary builds.

The implementation avoids runtime type discovery and reflection-based object serialization in protocol code. `Utf8JsonWriter`, `JsonDocument`, and `JsonNode` handle known and model-supplied JSON structures explicitly.

Publish on the target architecture:

```powershell
dotnet publish .\src\Wfx.Cli\Wfx.Cli.csproj -c Release -r win-x64
dotnet publish .\src\Wfx.Cli\Wfx.Cli.csproj -c Release -r win-arm64
```

GitHub Actions uses native Windows runners for each RID, executes the published binary with `--version`, and records binary size and warm startup time in the job summary. Do not copy measurements into this document until the exact commit's CI has run; measurements are toolchain- and commit-specific.

References: [.NET Native AOT deployment](https://learn.microsoft.com/dotnet/core/deploying/native-aot/) and [.NET 10 overview](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-10/overview).
