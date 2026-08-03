# Test Tool Cache

This directory is a local tool install target. Do not commit generated shims or `.store` payloads.

Install ILSpy for local test utilities with:

```powershell
dotnet tool install ilspycmd --tool-path .\.tests\tools
```

Use `dotnet tool update ilspycmd --tool-path .\.tests\tools` when a newer ILSpy version is needed.
