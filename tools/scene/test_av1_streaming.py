"""Small contract checks for bounded AV1 summaries; native acceptance stays separate."""
from fractions import Fraction
from unittest import TestCase, main
from unittest.mock import patch
import vidsc_av1_codec as av1


class StreamingEvidenceTests(TestCase):
    def test_digest_retains_only_running_state_and_binds_order(self):
        rows = av1.DigestRows()
        for ordinal in range(4096):
            rows.add(dict(sequence=ordinal), 8192)
        self.assertEqual(rows.report()["bytes"], 32 * 1024 * 1024)
        self.assertEqual(rows.report()["count"], 4096)
        self.assertEqual(set(vars(rows)), {"hash", "count", "bytes"})
        a, b = av1.DigestRows(), av1.DigestRows()
        for row in (dict(value=1), dict(value=2)): a.add(row)
        for row in (dict(value=2), dict(value=1)): b.add(row)
        self.assertNotEqual(a.report()["sha256"], b.report()["sha256"])

    def test_presentation_digest_uses_actual_successor_and_common_epoch(self):
        def row(pts):
            return dict(pts=pts, duration=41, width=16, height=16, pixelFormat="yuv420p",
                planeSha256=["a", "b", "c"], pixelSha256="pixels")
        source = av1.Presentations(Fraction(1, 1000), Fraction(10))
        output = av1.Presentations(Fraction(1, 1000), Fraction(0))
        with patch.object(av1.inspection, "frame_record", side_effect=lambda frame, tb: dict(frame)):
            for pts in (10000, 10042, 10083): source.add(row(pts))
            for pts in (0, 42, 83): output.add(row(pts))
        self.assertEqual(source.finish(10124)["sha256"], output.finish(124)["sha256"])
        self.assertEqual(source.digest.count, 3)
        self.assertEqual(source.last["duration"], 41)

    def test_nonterminal_packet_duration_is_bound_and_terminal_is_separate(self):
        packet = dict(payloadSha256="payload", size=12, pts=1000, dts=1000, duration=41,
            obuTypes=[6], sequenceHeaderSha256="sequence")
        original = av1.packet_summary_row(packet, Fraction(1, 1000), Fraction(1))
        terminal = av1.packet_summary_row(packet, Fraction(1, 1000), Fraction(1), True)
        packet["duration"] = 42
        self.assertNotEqual(original, av1.packet_summary_row(packet, Fraction(1, 1000), Fraction(1)))
        self.assertEqual(terminal, av1.packet_summary_row(packet, Fraction(1, 1000), Fraction(1), True))

    def test_matroska_omitted_duration_uses_proven_single_presentation_schedule(self):
        source = av1.TemporalUnits(Fraction(1, 1000), Fraction(0))
        output = av1.TemporalUnits(Fraction(1, 1000), Fraction(0), output=True)
        rounded = av1.TemporalUnits(Fraction(1, 1000), Fraction(0), output=True)
        for pts in (0, 42, 83):
            row = dict(payloadSha256="payload", size=12, pts=pts, dts=pts, duration=41,
                obuTypes=[6], sequenceHeaderSha256="sequence", shownPresentationCount=1)
            source.add(row); output.add(dict(row, duration=0)); rounded.add(dict(row, duration=42))
        expected = source.finish(124)
        self.assertEqual(expected, output.finish(124))
        self.assertEqual(expected, rounded.finish(124))
        self.assertEqual(source.raw_report()["omittedCount"], 0)
        self.assertEqual(output.raw_report()["omittedCount"], 3)
        for duration, shown in ((100, 1), (-1, 1), (0, 2)):
            broken = av1.TemporalUnits(Fraction(1, 1000), Fraction(0), output=True)
            with self.assertRaises(av1.Rejected):
                broken.add(dict(row, pts=0, duration=duration, shownPresentationCount=shown))
                broken.finish(41)


if __name__ == "__main__":
    main()
