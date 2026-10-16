"""Pure peak-pyramid checks; native decoding is verified separately in the packaged runtime."""
import unittest
from vidsc_waveform_job import peak_levels


class PeakTests(unittest.TestCase):
    def test_gap_remains_missing_and_partial_coverage_is_not_filled_with_silence(self):
        bins = [dict(index=0, firstPts=100, timeBaseNumerator=1, timeBaseDenominator=48000,
                     firstSampleOffset=0, coverageSamples=4, peaks=[[-1, .5], [.25, .75]]),
                dict(index=2, firstPts=108, timeBaseNumerator=1, timeBaseDenominator=48000,
                     firstSampleOffset=0, coverageSamples=2, peaks=[[.1, .2], [-.5, -.25]])]
        levels = peak_levels(bins, 4)
        self.assertEqual([0, 2], [b['index'] for b in levels[0]['bins']])
        self.assertEqual([4, 2], [b['coverageSamples'] for b in levels[1]['bins']])
        self.assertEqual(6, levels[2]['bins'][0]['coverageSamples'])
        self.assertEqual([[-1, .5], [-.5, .75]], levels[2]['bins'][0]['peaks'])
        self.assertEqual([[.1, .2], [-.5, -.25]], bins[1]['peaks'])


if __name__ == '__main__':
    unittest.main()
