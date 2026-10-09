# Bundled routing snapshot

These are real, non-empty SagerNet rule sets downloaded on 2026-10-01 (Asia/Shanghai). The exact download time, pinned upstream commits, file sizes and SHA-256 values are in `manifest.json`.

- `geosite-geolocation-cn.srs`: Chinese domains, from SagerNet/sing-geosite.
- `geosite-geolocation-!cn.srs`: non-Chinese domains, excluded by the Chinese IP bypass rule.
- `geoip-cn.srs`: Chinese IP ranges, from SagerNet/sing-geoip.

The adjacent JSON files were decompiled with bundled sing-box 1.14.2 and provide the readable rule definitions. The SRS files are unmodified upstream binaries. Upstream copyright and GPL-3.0-or-later license notices are retained in the two LICENSE files; the full GPL text is included here.

Generated configurations use `type: local`, `format: binary` and an absolute `path` to these files. Only rule sets referenced by enabled routing presets are included. No rule set download is required at startup, and an empty file is never substituted for a missing or damaged snapshot.

This is a fixed snapshot, not automatic online updates. Replacing files requires updating the manifest checksums and re-running the core configuration and rule matching checks. Source repositories:

- https://github.com/SagerNet/sing-geosite
- https://github.com/SagerNet/sing-geoip
