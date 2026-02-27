# FixSfi

`FixSfi` is a CLI tool that repairs **Smallworld VMDS Super File Index** (`.sfi`) files.

It scans for `.sfi` files, rebuilds the `.ds` path list from local files, and validates the resulting references.

## What Is An SFI File

A Smallworld VMDS Super File Index file is expected to contain:

1. Identifier line (file type marker)
2. Database identification code
3. One `.ds` file path per line

`FixSfi` preserves lines 1 and 2 and regenerates line 3+ from local files in the same directory.

## Behavior

- Recursively scans `*.sfi` from the target path.
- Skips backup `.sfi` files named:
  - `<name>.<digits>.sfi`
  - `<name>.<digits>_<digits>.sfi`
- For each active `.sfi`:
  - Finds local `<baseName>*.ds` files in the same directory.
  - Writes absolute local paths to the `.sfi` in deterministic order:
    - `<baseName>.ds`
    - numbered suffixes (`-1`, `-2`, `_1`, `_2`, ...)
    - fallback lexical order
- If changes are needed and `--apply` is used:
  - Renames original file to `<name>.<yyyyMMddHHmmss>.sfi`
  - Writes updated `.sfi`
- Validates `.ds` files referenced by the effective output:
  - file exists
  - file size > 0 bytes

## Usage

```bash
# Dry-run (default)
dotnet run --project FixSfi -- [path]

# Explicit dry-run
dotnet run --project FixSfi -- [path] --what-if

# Apply changes
dotnet run --project FixSfi -- [path] --apply

# Help
dotnet run --project FixSfi -- --help
```

Defaults:

- `path`: current directory
- mode: dry-run (`--what-if`)

## Output Codes

- `0`: success (no validation errors)
- `1`: completed, but one or more `.ds` validation errors were found
- `2`: invalid CLI arguments or invalid target path

## Build

```bash
dotnet build FixSfi.sln
```

## Publish Executables (Manual)

From repo root:

```bash
# Ubuntu x64 single-file
dotnet publish FixSfi/FixSfi.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o ./publish/linux-x64

# Windows x64 single-file
dotnet publish FixSfi/FixSfi.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish/win-x64
```

## GitHub Release Artifacts

This repository includes a release workflow at:

- `.github/workflows/release.yml`

It builds and uploads:

- `fixsfi-linux-x64.zip`
- `fixsfi-win-x64.zip`

When triggered by a published GitHub Release, those archives are attached to the Release as downloadable assets.

## Recommended Release Process

1. Ensure `main` is green (`dotnet build` passes).
2. Tag with semantic versioning (for example `v1.0.0`).
3. Create/publish a GitHub Release from that tag.
4. Wait for `Release` workflow to complete.
5. Share the Release URL with colleagues (assets are attached there).

## Best Practices

1. Keep `--what-if` as the first step in operations runbooks.
2. Commit only source; do not commit built binaries to git.
3. Use GitHub Releases as the binary distribution channel.
4. Keep release notes focused on behavior changes and operational impact.
5. Add a smoke test in CI for one sample `.sfi` as the project evolves.
