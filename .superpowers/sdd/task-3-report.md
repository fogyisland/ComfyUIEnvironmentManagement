status: DONE
commit SHA: b6a9bf2

## Files created
- `D:\ToolDevelop\ComfyUI\src-wpf\ComfyUI.Manager\Data\PyTorchVersionCache.cs`
- `D:\ToolDevelop\ComfyUI\tests-wpf\ComfyUI.Manager.Tests\Data\PyTorchVersionCacheTests.cs`

## Implementation
- Added one-hour TTL cache using `FetchedAt + Ttl < DateTimeOffset.UtcNow`.
- Added JSON file read/write with automatic parent-directory creation.
- `TryReadAsync` returns null on missing, expired, corrupt, or failed reads.
- `WriteAsync` swallows IO/serialization failures as required.

## Verification
- TDD red phase: focused missing-file test failed before implementation because `PyTorchVersionCache` did not exist.
- TDD green phase: focused missing-file test passed after minimal implementation.
- Final command: `dotnet test D:/ToolDevelop/ComfyUI/tests-wpf/ComfyUI.Manager.Tests/ComfyUI.Manager.Tests.csproj --filter "FullyQualifiedName~PyTorchVersionCacheTests" --no-restore`
- Result: 7/7 PASS.

## Concerns
- Existing unrelated xUnit analyzer warnings remain in `CatalogRefreshServiceNoTokenTests.cs` and `BulkUpdateOrchestratorTests.cs`; no new warnings from the cache implementation.

## T3 review fix
- Added private `JsonSerializerOptions` with `PropertyNameCaseInsensitive = true` to `PyTorchVersionCache`.
- Applied the options to both JSON deserialization and serialization.

## Verification
- `PyTorchVersionCacheTests`: 7/7 PASS.
- Full suite: 203 passed, 1 skipped, 0 failed (204 total).
