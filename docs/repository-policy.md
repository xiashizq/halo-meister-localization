# Repository and release policy

`NicmeisteR/halo-meister` is the canonical home for source, documentation,
issues, tags, and release downloads. The former `halo-meister-releases`
repository is archived and retained only so old links continue to work.

## Versioning

- `Directory.Build.props` contains the canonical SemVer in `<Version>`.
- A release tag must be exactly `v<Version>` and point at the commit containing
  that version.
- The release workflow rejects mismatched tags and publishes its assets to this
  repository's GitHub Releases page.
- Release body text comes from `.github/release-notes/<Version>.md` (required).
- The app checks GitHub's latest stable release from **Community & links** and
  compares it with its own informational version.

## What belongs in Git

Track the source, reproducible documentation, project metadata, workflows, and
runtime assets required to build a working package from a fresh clone. The
current native bridge and exporter binaries remain tracked because the release
workflow does not build those toolchains yet.

Do not commit generated build or publish output, IDE state, machine-specific
paths, personal save data, local probes, logs, backups, crash dumps, secrets, or
historical native bridge binaries. Keep those files locally under ignored paths.
When adding a new local workflow, update `.gitignore` before generating its
outputs.
