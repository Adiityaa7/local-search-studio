LOCAL SEARCH STUDIO 1.0.0

An independent, free Windows desktop interface for the XERJ search engine.
This edition performs local keyword search. It does not enable neural models,
cloud reranking, an AI chat service, accounts, advertisements, or analytics.

Requirements
Windows 10 version 2004 (build 19041) or newer, Windows 11; x64 processor with
AVX2. ARM64 and older non-AVX2 processors are not supported by this release.
The .NET 10.0.12 desktop runtime and XERJ v1.0.0-rc.95 engine are bundled.
Allow disk space for indexes as well as the application; large datasets need
more memory, disk space, and time. Index only folders you need.

Use
1. Open LocalSearchStudio.exe from the extracted portable folder, or install
   the Microsoft Store version when it becomes available.
2. Add one folder using the button, Ctrl+O, or drag and drop.
3. Wait for indexing, then search keywords or an exact phrase in quotes.
4. Select a passage to preview it; Show in folder selects its source in Explorer.
5. Use My folders > Refresh after changing source files. Unchanged files are
   reused. Stop indexing cancels a job; Refresh resumes it later.
Ctrl+K focuses search; Escape clears it. Search returns up to 60 passages.
Previews are indexed snapshots, not live source-file contents.

Performance and safeguards
220 ms input debounce and cancellation of superseded searches; bounded
24-query session cache; virtualized results; 8 MB response limit and 32,000
character preview cap. Incremental indexing is provided by XERJ. A Windows
job object ties child processes to the app, with a clean flush at shutdown.
Only authenticated loopback endpoints are used; no network-wide listener.
App data is restricted to the current Windows user, but is not encrypted by
this app. Operating-system and disk security remain applicable.

Build
Install .NET SDK 10.0.402 and Windows SDK 10.0.26100.0. From desktop/ run:
  powershell -File scripts/Build.ps1
The script pins and verifies the official engine archive, restores locked
NuGet dependencies, and produces a self-contained x64 build and unsigned
Store MSIX. It does not modify the XERJ engine. Use -DotNet for another path
to the SDK executable, -MakeAppx for the Windows SDK path, and
-OutputDirectory for a destination. The manifest identity is for the
Adiityaaa7 publisher; independent redistributors need their own identity.

Test
From the repository root after preparing desktop/runtime/xerj.exe:
  dotnet run --project desktop/Studio.Tests -c Release -- desktop/runtime/xerj.exe <new-test-directory>
Tests use generated sample files and a separate private app-data directory.
They verify authenticated access, traversal protection, incremental changes
and deletions, query-cache invalidation, restart persistence, refresh after
restart, and cancellation. Do not point tests at an existing user library.

Attribution and licensing
See notices/DESKTOP-NOTICE.txt, XERJ-NOTICE.txt, XERJ-LICENSE.txt,
THIRD-PARTY-NOTICES.txt, DOTNET-* and WINDOWS-DESKTOP-LICENSE.txt.
MPL component source archives are included in notices/sources/.
Desktop changes were added 15 October 2026 under Apache License 2.0.
Original engine source: https://github.com/xerj-org/xerj at v1.0.0-rc.95.
Local Search Studio is not affiliated with or endorsed by XERJ.

This release retains the upstream engine's release-candidate version.
The small-fixture test timings are not a benchmark of all file types or an
assertion that the engine is faster than upstream XERJ.
