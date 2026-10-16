"""Bounded canonical PCM and packet identity contracts, without a native decoder."""
from fractions import Fraction
from collections import deque
from types import SimpleNamespace
import struct
from unittest import TestCase, main
from unittest.mock import patch

import vidsc_audio_streaming as audio


class Plane(bytearray):
    @property
    def buffer_size(self):
        return len(self)


class Budget:
    def check(self):
        pass


def frame(first, count, value=0.25):
    return SimpleNamespace(pts=first, time_base=Fraction(1, 48000), sample_rate=48000,
        samples=count, format=SimpleNamespace(name="fltp"),
        layout=SimpleNamespace(name="5.1", channels=range(6)), is_corrupt=False,
        planes=[Plane(struct.pack("<f", value) * count) for _ in range(6)])


class AudioStreamingTests(TestCase):
    def test_aac_preserves_bounded_source_start_history(self):
        prefix = deque(dict(sequence=i, duration=1024, size=100) for i in range(12))
        samples, size = audio._trim_prefix(prefix, 12 * 1024, 1200, 1024, Fraction(1, 48000), "aac")
        self.assertEqual((prefix[0]["sequence"], samples, size), (0, 12 * 1024, 1200))
        prefix = deque(dict(sequence=i, duration=1024, size=100) for i in range(65))
        samples, size = audio._trim_prefix(prefix, 65 * 1024, 6500, 1024, Fraction(1, 48000), "aac")
        self.assertEqual((prefix[0]["sequence"], len(prefix), samples, size), (1, 64, 64 * 1024, 6400))
        samples, size = audio._trim_prefix(prefix, samples, size, 1024, Fraction(1, 48000), "ac3")
        self.assertEqual((prefix[0]["sequence"], len(prefix), samples, size), (64, 1, 1024, 100))

    def test_long_aac_prefix_is_used_only_when_exact_pcm_needs_it(self):
        rational = audio.inspection.rational
        first = dict(sequence=46, pts=47104, duration=1024)
        available = dict(sequence=0, pts=0, duration=1024)
        last = dict(sequence=140, pts=143360, duration=1024)
        reference = dict(first=first, last=last, availableFirst=available,
            configuration={"codec": "aac"}, pcm={"sha256": "source"}, timeBase=rational(Fraction(1, 48000)),
            sourceCoverageEnd=rational(Fraction(3)), retainedContextUnits=1, retainedContextBytes=100,
            availableContextUnits=47, availableContextBytes=4700, requiredContextSamples=1024,
            sourceSuccessorObserved=True, demuxEof=False)
        fresh = dict(reference, packets={}, sideData={}, decodedStart=rational(Fraction(0)),
            decodedEnd=rational(Fraction(3)), declaredLeadingSkipSamples=0, declaredTrailingSkipSamples=0)
        with patch.object(audio, "_scan", side_effect=[dict(reference), fresh]) as scan:
            result = audio.streaming_read("fixture", 1, Fraction(1), Fraction(3), Budget())
            self.assertEqual(result["firstPacket"], 46)
            self.assertEqual(scan.call_count, 2)
        with patch.object(audio, "_scan", side_effect=[dict(reference), dict(fresh, pcm={"sha256": "different"}), fresh]) as scan:
            result = audio.streaming_read("fixture", 1, Fraction(1), Fraction(3), Budget())
            self.assertEqual((result["firstPacket"], result["retainedContextUnits"]), (0, 47))
            self.assertEqual(scan.call_args.args[-1], (0, 140))

    def test_pcm_digest_is_frame_boundary_and_epoch_independent(self):
        a = audio.Projection(Fraction(1), Fraction(2), "5.1")
        b = audio.Projection(Fraction(0), Fraction(1), "5.1")
        for first in range(48000, 96000, 1024):
            a.collect(frame(first, min(1024, 96000 - first)), Fraction(1, 48000), Budget())
        for first in range(0, 48000, 1536):
            b.collect(frame(first, min(1536, 48000 - first)), Fraction(1, 48000), Budget())
        self.assertEqual(a.finish(), b.finish())
        self.assertEqual(a.digest.count, 12)
        self.assertTrue(all(len(p) == 0 for p in a.planes))

    def test_pcm_storage_is_bounded_beyond_old_byte_cap(self):
        projection = audio.Projection(Fraction(0), Fraction(16), "5.1")
        for first in range(0, 48000 * 16, 4096):
            projection.collect(frame(first, min(4096, 48000 * 16 - first)), Fraction(1, 48000), Budget())
            self.assertLessEqual(sum(len(p) for p in projection.planes), 6 * 4096 * 4)
        self.assertGreater(projection.finish()["bytes"], 16 * 1024 * 1024)

    def test_gap_shortfall_nonfinite_and_pcm_mutation_are_rejected(self):
        projection = audio.Projection(Fraction(0), Fraction(1, 48000), "5.1")
        with self.assertRaisesRegex(Exception, "IncompleteAudibleCoverage"):
            projection.finish()
        with self.assertRaisesRegex(Exception, "NonfiniteAudioSample"):
            projection.collect(frame(0, 1, float("nan")), Fraction(1, 48000), Budget())
        a = audio.Projection(Fraction(0), Fraction(2, 48000), "5.1")
        a.collect(frame(0, 1), Fraction(1, 48000), Budget())
        with self.assertRaisesRegex(Exception, "AudioDecodeGapOrOverlap"):
            a.collect(frame(2, 1), Fraction(1, 48000), Budget())
        b = audio.Projection(Fraction(0), Fraction(2, 48000), "5.1")
        b.collect(frame(0, 2, 0.5), Fraction(1, 48000), Budget())
        a.collect(frame(1, 1), Fraction(1, 48000), Budget())
        self.assertNotEqual(a.finish(), b.finish())

    def test_packets_bind_duration_order_payload_and_common_epoch(self):
        a, b = audio.Packets(Fraction(1, 48000), Fraction(1)), audio.Packets(Fraction(1, 48000), Fraction(0))
        row = dict(pts=48000, dts=48000, duration=1024, payloadSha256="fixture", size=20)
        a.add(row); b.add(dict(row, pts=0, dts=0))
        self.assertEqual(a.report(), b.report())
        with self.assertRaisesRegex(Exception, "AudioPacketGapOrOverlap"):
            a.add(row)

    def test_output_validation_binds_complete_packet_inventory_and_extents(self):
        rational = audio.inspection.rational
        expected = dict(configuration={}, packetSummary={"count": 2}, decodedSummary={"sha256": "pcm"},
            sourceCoverageEnd=rational(Fraction(3)), audibleStart=rational(Fraction(1)),
            audibleEnd=rational(Fraction(3)), declaredLeadingSkipSamples=0, declaredTrailingSkipSamples=0)
        actual = dict(configuration={}, containerFormat="mp4", packets={"count": 2}, pcm={"sha256": "pcm"},
            decodedStart=rational(Fraction(0)), decodedEnd=rational(Fraction(2)),
            declaredLeadingSkipSamples=0, declaredTrailingSkipSamples=0)
        with patch.object(audio, "_scan", return_value=actual) as scan:
            self.assertTrue(audio.streaming_validate("fixture", 0, expected, Fraction(1), Fraction(3), Budget())["validated"])
            self.assertTrue(scan.call_args.kwargs["scan_all"])
        for changed, reason in ((dict(packets={"count": 3}), "ActualOutputAudioPacketRejected"),
                (dict(decodedEnd=rational(Fraction(4))), "ActualOutputAudioExtentRejected")):
            with patch.object(audio, "_scan", return_value=dict(actual, **changed)):
                with self.assertRaisesRegex(Exception, reason):
                    audio.streaming_validate("fixture", 0, expected, Fraction(1), Fraction(3), Budget())
        expected["audibleStart"] = rational(Fraction(9, 10))
        with patch.object(audio, "_scan", return_value=actual):
            with self.assertRaisesRegex(Exception, "ActualOutputAudioExtentRejected"):
                audio.streaming_validate("fixture", 0, expected, Fraction(1), Fraction(3), Budget())
        with patch.object(audio, "_scan", return_value=dict(actual, declaredLeadingSkipSamples=4800)):
            receipt = audio.streaming_validate("fixture", 0, expected, Fraction(1), Fraction(3), Budget())
            self.assertEqual(receipt["editSkipSamples"], 4800)


if __name__ == "__main__":
    main()
