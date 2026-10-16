# vidsc local Release bundle notices

**Distribution status: private-local-only; redistribution is not cleared.**
The observed runtime manifest records actual files and hashes. `DISTRIBUTION.md` describes
the missing source/build/replacement materials; this notice is not a substitute for those
materials or the complete licenses. Upstream license rights remain unchanged.

This bundle uses Flyleaf (SuRGeoNix and contributors), FFmpeg (the FFmpeg developers),
CPython (Python Software Foundation and contributors), PyAV, PySceneDetect, NumPy,
OpenCV, Click, Colorama, platformdirs, tqdm, pip and the managed dependency closure
identified by the authored and observed manifests. Copyrights remain with their owners.

Managed Flyleaf 3.11.11 with vidsc presentation-hook modifications is LGPL-3.0-or-later.
The separately pinned playback FFmpeg libraries actually report GPL-3.0-or-later;
the CLI FFmpeg build also enables GPL/version3. PyAV bindings are BSD-3-Clause, but
its wheel includes a separate FFmpeg/native dependency set whose full licensing and
source correspondence still need review. Do not treat these three FFmpeg sets as one
interchangeable runtime or assume a package-level license covers all native dependencies.

Keep `scene/python/LICENSE.txt`, wheel license trees under
`scene/python/Lib/site-packages/*.dist-info`, OpenCV third-party notices, NumPy notices
and pip's vendored notices with the runtime. Keep the Flyleaf LGPLv3 and FFmpeg GPLv3
texts supplied in the release notices directory. Managed package notices remain part of
the outstanding redistribution review. Exact pins are recorded in
`runtime-manifest.json`; the observed manifest identifies the delivered file contents.

Upstream references:

- [Flyleaf](https://github.com/SuRGeoNix/Flyleaf)
- [FFmpeg license and source guidance](https://ffmpeg.org/legal.html)
- [GPLv3](https://www.gnu.org/licenses/gpl.en.html), [LGPLv3](https://www.gnu.org/licences/lgpl.html)
- [Python license and incorporated-software notices](https://docs.python.org/3.14/license.html)
- [PyAV license](https://raw.githubusercontent.com/PyAV-Org/PyAV/v19.0.1/LICENSE.txt)

The Microsoft .NET 10 Desktop Runtime is a separate prerequisite and is not bundled.
