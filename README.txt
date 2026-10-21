FOLDERHUNT

Store title: FolderHunt: Search Inside Files
Previously published as Local Search Studio. Version 1.0.1 updates the name
and branding. Existing installations retain their indexes and settings.

A free, independent Windows desktop interface powered by XERJ.
Native WPF UI, local keyword search, folder indexing, passage previews,
keyboard shortcuts, incremental refresh, and a bounded session query cache.

Windows 10 build 19041 or newer / Windows 11, x64 with AVX2 required.
ARM64 is not supported. No account, analytics, advertisements, model download,
or cloud search service is enabled by this desktop edition.

SOURCE AND BUILD
The complete desktop integration is in desktop/. See desktop/README.txt for
build instructions, shortcuts, supported behavior, requirements and tests.
Build from desktop/ using powershell -File scripts/Build.ps1 after installing
.NET SDK 10.0.402 and Windows SDK 10.0.26100.0. The script downloads the pinned
official XERJ v1.0.0-rc.95 Windows binary and verifies its SHA-256 checksum.

The XERJ engine is unmodified. Its complete source and history are maintained
upstream at https://github.com/xerj-org/xerj/tree/v1.0.0-rc.95
Source commit: 8b495d34c1183ce3d76579db527c6a804bf2845e
This repository contains the independent desktop integration and required
notices and component sources; it is not the official XERJ repository.

SUPPORT AND PRIVACY
Report app problems at https://github.com/Adiityaa7/local-search-studio/issues
Please do not attach private documents, indexes, keys or personal paths.
The privacy policy is in privacy.html and in the app's Store listing.
Searches and indexing run locally. Indexes include extracted document text;
the app does not encrypt them. See the privacy policy for data removal.

MICROSOFT STORE
Product ID: 9NLP1K2WZ8NS
https://apps.microsoft.com/detail/9NLP1K2WZ8NS
The original version is live on Microsoft Store as Local Search Studio.
The FolderHunt name appears after the version 1.0.1 update is approved and
published. This repository's current branding is ahead of that Store update.

CREDITS AND LICENSES
Desktop additions: Copyright 2026 Adiityaaa7, Apache License 2.0.
XERJ: Copyright 2026 The XERJ Authors, Apache License 2.0.
This product includes software developed by the XERJ project (https://xerj.org).
FolderHunt is not affiliated with or endorsed by the XERJ project.
See LICENSE, NOTICE and desktop/notices/ for complete bundled credits,
third-party notices, .NET terms and unmodified MPL component source archives.
The upstream tree-sitter-groovy 0.1.2 licensing-file limitation is documented
in NOTICE and desktop/Validation.txt; its original source archive is included.

VALIDATION
See desktop/Validation.txt for the exact integration-test scope and timings.
Small-fixture timing results are not a general engine-performance benchmark.
The engine retains its upstream release-candidate version.
