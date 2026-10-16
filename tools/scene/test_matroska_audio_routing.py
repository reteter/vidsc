"""Keep audio policy and receipt positions independent of the video codec."""
from argparse import Namespace
from fractions import Fraction
from pathlib import Path
from types import SimpleNamespace
from unittest import TestCase, main
from unittest.mock import Mock, patch
import json

import vidsc_matroska_copy_job as mux
import vidsc_audio_streaming as compact


def container(streams):
    value = Mock()
    value.__enter__ = Mock(return_value=SimpleNamespace(streams=streams, metadata={},
        chapters=lambda: [], format=SimpleNamespace(name="matroska,webm")))
    value.__exit__ = Mock(return_value=False)
    return value


def track(codec):
    return dict(configuration=dict(codec=codec))


class MatroskaAudioRoutingTests(TestCase):
    def test_avc_hevc_qualification_uses_each_audio_codec_policy(self):
        codecs = ["eac3", "aac", "ac3", "eac3"]
        streams = [SimpleNamespace(codec_context=SimpleNamespace(name=codec)) for codec in codecs]
        for video_codec in ("h264", "hevc"):
            with self.subTest(codec=video_codec):
                args = Namespace(codec=video_codec, streaming=True, container="matroska", job_id="routing",
                    input="source", output_media="owned-output", start_pts=0, end_pts=1,
                    manifest_id=mux.inspection.sha(json.dumps({}, sort_keys=True).encode()),
                    audio_stream=list(range(4)), phase="write")
                qualified = dict(success=True, packetSummary={}, timeBase=dict(numerator=1, denominator=1))
                mapping = dict(subtitles=[], chapters=[])
                with patch.dict("sys.modules", av=SimpleNamespace(open=Mock(return_value=container(streams)))), \
                        patch.object(mux.video, "execute", return_value=qualified), \
                        patch.object(mux, "read_mapping", return_value=(mapping, {})), \
                        patch.object(compact, "streaming_read", side_effect=[track("aac"), track("ac3")]) as new_read, \
                        patch.object(mux.audio, "streaming_read", side_effect=[track("eac3"), track("eac3")]) as old_read, \
                        patch.object(mux, "write_streaming", side_effect=RuntimeError("stop before native mux")) as writer:
                    mux.execute_guarded(args)
                self.assertEqual([row["configuration"]["codec"] for row in mapping["audio"]], codecs)
                self.assertEqual([call.args[1] for call in old_read.call_args_list], [0, 3])
                self.assertEqual([call.args[1] for call in new_read.call_args_list], [1, 2])
                writer.assert_called_once()

    def test_mixed_output_validation_preserves_stream_receipt_indices(self):
        codecs = ["eac3", "aac", "ac3", "eac3"]
        args = Namespace(codec="hevc", streaming=True, container="matroska")
        streams = [SimpleNamespace(type="video", metadata={}, disposition=0)] + [None] * len(codecs)
        mapping = dict(sourceMetadata={}, videoMetadata={}, videoDisposition=0, chapters=[], subtitles=[],
            audio=[track(codec) for codec in codecs])
        receipts = [dict(outputStreamIndex=2), dict(outputStreamIndex=3)]
        with patch.dict("sys.modules", av=SimpleNamespace(open=Mock(return_value=container(streams)))), \
                patch.object(compact, "streaming_validate", side_effect=receipts) as new_validate, \
                patch.object(mux.audio, "streaming_validate") as old_validate:
            actual = mux.validate_metadata(Path("output"), mapping, args, Fraction(0), Fraction(1), Mock())
        self.assertEqual(actual, [None, receipts[0], receipts[1], None])
        self.assertEqual([call.args[1] for call in old_validate.call_args_list], [1, 4])
        self.assertEqual([call.args[1] for call in new_validate.call_args_list], [2, 3])


if __name__ == "__main__":
    main()
