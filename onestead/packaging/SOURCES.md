# Runtime components and corresponding sources

This is an aggregation of separately licensed programs. Preserve this file,
NOTICE, LICENSES, and Git's original license directories when redistributing.

| Component | Fixed version | Source and rebuilding |
| --- | --- | --- |
| Onestead | 0.1.0 | `SOURCES/onestead-0.1.0-source.zip` contains the exact application source, resources, tests and recipes used for this artifact; Apache-2.0. |
| CPython | 3.12.13 | <https://www.python.org/ftp/python/3.12.13/Python-3.12.13.tar.xz>; PSF and bundled third-party terms in `LICENSES/CPython-LICENSE.txt`. |
| Python packages and build bootloader | `LICENSES/python-packages.json` and `SOURCES/build-recipes/requirements-build.lock` | Each inventory item retains upstream project URLs and installed license files. Versioned sdists/wheels are available from `https://pypi.org/project/<name>/<version>/#files`; retrieve each matching sdist to obtain source. PyInstaller source and bootloader build instructions: <https://github.com/pyinstaller/pyinstaller/tree/v6.22.3/bootloader>. |
| Windows MinGit | 2.56.1.windows.1 (64-bit UCRT) | <https://github.com/git-for-windows/git/releases/tag/v2.56.1.windows.1>. The binary checksum is pinned in `git-sources.json` from the official release table. Git's corresponding Windows source: <https://github.com/git-for-windows/git/archive/refs/tags/v2.56.1.windows.1.tar.gz>. Build/install scripts: <https://github.com/git-for-windows/build-extra>. |
| macOS/Linux Git | 2.56.1 | <https://www.kernel.org/pub/software/scm/git/git-2.56.1.tar.xz>; checksum from <https://www.kernel.org/pub/software/scm/git/sha256sums.asc>. The full unchanged source archive accompanies native artifacts in `SOURCES/`. Build recipe: `SOURCES/build-recipes/build.py`. GPL-2.0. |
| macOS/Linux static zlib | 1.3.1 | <https://zlib.net/fossils/zlib-1.3.1.tar.gz>; pinned SHA-256 in the recipe. The unchanged source archive accompanies native artifacts. zlib license in `LICENSES/`. |

MinGit is preserved in full, including `git/etc/package-versions.txt`, which
records the upstream MSYS2/UCRT build package versions (a superset of the files
in MinGit). Corresponding
source packages and recipes are provided by the Git for Windows project at
<https://github.com/git-for-windows/MSYS2-packages> and
<https://github.com/git-for-windows/MINGW-packages>, with archived source packages
at <https://sourceforge.net/projects/git-for-windows/files/>. Match the name,
version and release from `package-versions.txt` (including patch sources and
PKGBUILD) rather than using a newer upstream release. Git for Windows documents
its source distribution at <https://gitforwindows.org/> and the release page.
The original MinGit dependency license texts are preserved in both the Git
tree and LICENSES. These retrieval instructions do not grant permission to
omit corresponding sources when a chosen redistribution method requires them.

Native Git is built on the target CPU without package-manager Git or developer
tools at runtime. zlib is linked statically; Git uses its bundled SHA code and
omits HTTP, Tcl/Tk, Perl, Python, gettext, expat and iconv integrations. Local
init, commit, history, revert, archive, bundle and clone remain available and
are exercised by the runtime smoke script. Only operating-system ABI libraries
are expected dynamically (`libSystem` on macOS; glibc/pthread on Ubuntu). CI
records actual linkage alongside each artifact. Compiler/make are build-time
tools only. This is a local runtime, not a replacement for general network Git.
