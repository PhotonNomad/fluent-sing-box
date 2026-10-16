# Bundled routing snapshot

These are real, non-empty SagerNet rule sets downloaded on 2026-10-01 (Asia/Shanghai). The exact download time, pinned upstream commits, file sizes and SHA-256 values are in `manifest.json`.

- `geosite-geolocation-cn.srs`: Chinese domains, from SagerNet/sing-geosite.
- `geosite-geolocation-!cn.srs`: non-Chinese domains, excluded by the Chinese IP bypass rule.
- `geoip-cn.srs`: Chinese IP ranges, from SagerNet/sing-geoip.

The adjacent JSON files were decompiled with bundled sing-box 1.14.2 and provide the readable rule definitions. The SRS files are unmodified upstream binaries. Upstream copyright and GPL-3.0-or-later license notices are retained in the two LICENSE files; the full GPL text is included here.

Generated configurations use `type: local`, `format: binary` and an absolute `path` to these files. Only rule sets referenced by enabled routing presets are included. No rule set download is required at startup, and an empty file is never substituted for a missing or damaged snapshot.

This directory remains a fixed distribution snapshot. The client provides a manual Update button for these three rule sets. Updates are stored separately under the user's `sing-box/rule-sets/` directory; the distributed files and this manifest are not changed. Each update pins the upstream `rule-set` branch commits, checks downloaded files against GitHub's Git blob identity and size, validates the binaries with the selected core's decompiler, and records local SHA-256 values and readable definitions with the license notices. A complete snapshot becomes active only after all three files pass; already running cores retain their existing snapshot until restarted. Custom rule-set paths are not replaced. Source repositories:

- https://github.com/SagerNet/sing-geosite
- https://github.com/SagerNet/sing-geoip
