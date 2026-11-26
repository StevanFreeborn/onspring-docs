# onspring-docs

A fast, deterministic .NET 11 Native AOT CLI that scrapes an Onspring Help Center into an agent-optimized Markdown knowledge base.

## Features

- **Pure HTTP authentication & parallel fetching:** Signs in via `/Public/Login` over `HttpClient`, parses MadCap Flare's `HelpSystem.xml` and `OnspringTOC*.js` chunks directly, and downloads all topics in bounded parallel with retries plus 1-hop discovery of linked internal pages.
- **Agent-ready output layout:**
  - Normalized hierarchical kebab-case `.md` files with YAML frontmatter (`id`, `title`, `section`, `doc_type`, `audience`, `toc_paths`, `source_path`, `summary`).
  - `AGENTS.md`: Navigation instructions for AI coding/research agents.
  - `INDEX.md`: Full hierarchical table of contents with audience tags, one-line summaries, and segregated `release-notes/` and `videos/` sections.
  - `llms.txt`: Compact discovery entry point.
  - `manifest.json`: Sorted machine-readable catalog (`headings`, `links_to`, `word_count`, `char_count`, `sha256`).
- **Clean MadCap Flare conversion:** Expands collapsed `.MCDropDown` / `.MCExpanding` / `.MCToggler` widgets (preserving full FAQ answers), decodes Cloudflare email protection, formats monospace formula spans and callouts, simplifies tables for GFM, strips dead relative images, and rewrites internal `.htm` links and `#anchor` slugs to relative `.md` targets.
- **Byte-for-byte deterministic & atomic:** Uses UTF-8 (no BOM), LF (`\n`) line endings, zero volatile scrape timestamps, and atomic staging directory swaps so repeated runs against an unchanged Help Center produce identical hashes and zero stale files.
- **Standalone Native AOT binary:** Publishes as a single `onspring-docs.exe` with no runtime or browser dependencies.

## Configuration

Copy [`OnspringDocs/appsettings.example.json`](OnspringDocs/appsettings.example.json) to `appsettings.json` and fill in your instance URL and credentials:

```json
{
  "Onspring": {
    "BaseUrl": "https://your-instance.onspring.com",
    "Username": "your-username",
    "Password": "your-password"
  }
}
```

Configuration precedence (highest to lowest):

1. Environment variables: `ONSPRING__BASEURL`, `ONSPRING__USERNAME`, `ONSPRING__PASSWORD`
2. `--config <path>`
3. `./appsettings.json` (current working directory)
4. `<exe-dir>/appsettings.json`
5. `<exe-dir>/../appsettings.json`

## Usage

```powershell
onspring-docs [options]
```

| Option | Description |
| :--- | :--- |
| `--output <dir>` | Target output directory (default: `docs`) |
| `--config <path>` | Path to an explicit JSON config file |
| `--concurrency <1-32>` | Max parallel HTTP topic fetches (default: `12`) |
| `--exclude-release-notes` | Exclude all release notes topics from output |
| `--recent-releases <N>` | Keep only the `N` newest versioned release note pages (plus overview) |
| `--exclude-videos` | Exclude video stub pages from output |
| `--toc-only` | Disable 1-hop discovery of non-TOC internal `.htm` links |
| `--save-raw-html <dir>` | Optional directory to save raw fetched HTML for debugging |
| `-h`, `--help` | Show help message and exit |

## Build, Test & Native AOT Publish

```powershell
# Build solution
dotnet build --configuration Release

# Run unit and determinism tests
dotnet test --configuration Release

# Publish standalone Native AOT executable (Windows x64)
dotnet publish OnspringDocs/OnspringDocs.csproj -c Release -r win-x64 -o "$env:USERPROFILE\onspring-docs\bin"
```
