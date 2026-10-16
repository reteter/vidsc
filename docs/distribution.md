# Distribution decision and binary obligations

The current Release bundle is **private-local-only**. Its manifest records
`redistributionEligible: false`. Building, relocating and testing it locally is supported;
uploading the bundle to a public Release, CI artifact or download service is not cleared.
This decision does not restrict rights granted by upstream licenses. It records that this
project has not assembled the materials needed to convey these particular binaries.

## What is actually present

The authored runtime manifest pins inputs. The generated observed manifest inventories the
delivered files and hashes; neither is a corresponding-source archive or a license grant.
Keep the three FFmpeg sets separate:

| Delivered component | Actual identity and license evidence | Remaining distribution work |
| --- | --- | --- |
| Managed Flyleaf | Vendored upstream `0f061769c47d50dfdae5c667435bf6548a0dc121`, local presentation-hook changes, `3.11.11+vidsc.hook.1`; LGPL-3.0-or-later | Preserve copyright and LGPL/GPL texts, publish the exact modified library source and build instructions, and establish a tested replacement/relink route. |
| Playback FFmpeg | Flyleaf 3.11.11 native archive, SHA256 `de50d40ba8f471fbbb005b33deaf9dde172fca44cd123cbe0835b9822dd4a3b1`; ABI avcodec/avformat 63, avutil 61. Its native `avutil_license()` reports **GPL version 3 or later**; configuration enables GPL and version3. | Obtain corresponding patched FFmpeg source, external dependency sources/notices and build scripts for this exact archive. Review the GPL implications of the application loading these libraries in-process; managed Flyleaf's LGPL label does not clear the native combination. |
| CLI FFmpeg/ffprobe | `N-121120-gc74181a04b-20250918`; shared DLL build with `--enable-gpl --enable-version3`, including x264/x265. ABI avcodec/avformat 62, avutil 60. | Obtain exact FFmpeg and enabled dependency source/build inputs, full license texts and an effective source delivery arrangement. A version string and upstream homepage do not establish correspondence. |
| PyAV native FFmpeg | PyAV `19.0.1` Windows wheel, FFmpeg ABI 63/61. Native `avutil_license()` reports LGPL version 3 or later, while configuration enables libx264/libx265. | Resolve the actual bundled encoder/dependency licensing and corresponding-source provenance. The PyAV BSD license covers its bindings and does not certify every native binary in the wheel. Do not infer clearance from the native license string alone. |
| Python and wheels | CPython 3.14.5 embeddable; scenedetect 0.7.2, av 19.0.1, NumPy 2.5.4, opencv-python 5.0.0.93, click 8.5.0, colorama 0.4.6, platformdirs 4.13.0, tqdm 4.70.1; bootstrap pip 26.2.1 is also retained. | Retain CPython's license and incorporated-software acknowledgments, each wheel's notices and all native dependency notices. Audit wheel contents, not only top-level package metadata. |
| Managed dependencies | Flyleaf.FFmpeg.Bindings and the Vortice dependency closure pinned by the solution | Inventory actual published assemblies and preserve their package license/copyright materials. Confirm vidsc's own licensing decision before external distribution. |

The scene runtime preserves wheel `*.dist-info` license trees, OpenCV's
`LICENSE-3RD-PARTY.txt`, NumPy's bundled notices, and pip's vendored notices. These are
useful inputs to the review, not proof that the complete native dependency closure is covered.
The .NET Desktop Runtime is installed separately and is not delivered in this bundle.

## Requirements before an external binary release

For GPL-covered binaries, select and implement a permitted source-delivery route, including
the exact modified sources and scripts needed to build/install them. For a network download,
provide equivalent source access with clear directions beside the binaries and maintain that
access. Preserve notices and license texts; assess the licensing of the in-process combination.
See [GPLv3 sections 1, 5 and 6](https://www.gnu.org/licenses/gpl.en.html).

For LGPL-covered libraries, preserve notices and both LGPL/GPL texts, provide corresponding
library sources and changes, permit debugging library modifications, and establish a suitable
shared-library replacement mechanism or the required relink materials. Loose DLL placement
alone does not demonstrate replacement works. See
[LGPLv3 section 4](https://www.gnu.org/licences/lgpl.html).

For Python, preserve the PSF agreement/copyright and incorporated-software notices; record
modifications when distributed. See [Python's license](https://docs.python.org/3.14/license.html).
Preserve the actual BSD/MIT/Apache/MPL and other terms shipped inside each wheel, including
their bundled native libraries. [PyAV's source license](https://raw.githubusercontent.com/PyAV-Org/PyAV/v19.0.1/LICENSE.txt)
and [upstream wheel documentation](https://github.com/PyAV-Org/PyAV/blob/master/docs/overview/installation.rst)
must be read separately from native FFmpeg obligations.

Record the reviewed source archives, hashes, build recipes, dependency notices, replacement
test and download/source locations in the distribution manifest before changing eligibility.
Review codec patent and third-party terms for the intended distribution separately. The
[FFmpeg legal guidance](https://ffmpeg.org/legal.html) distinguishes license configurations
and corresponding sources; it does not grant a universal patent clearance.

## Local package behavior

Keep the complete `app` directory together. Tools are resolved relative to that directory;
`native/flyleaf`, `tools/ffmpeg`, and `scene/python` have separate roles. This delivery uses
ordinary files and no single-file extraction. A fresh `DOTNET_BUNDLE_EXTRACT_BASE_DIR`
therefore remains unused; scene/output work uses explicitly owned writable directories.
Python's embedded runtime is relocatable and does not require a development Python install.
No runtime environment override or development PATH entry is needed for a complete bundle.

Backend relocation acceptance covers packaged scenes and both export modes. GUI open/play,
save/reopen, displayed-frame behavior and shutdown require separately authorized desktop
verification and remain outside the non-GUI delivery check.
