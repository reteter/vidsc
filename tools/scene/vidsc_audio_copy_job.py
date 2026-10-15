"""JSON-only bounded production audio-copy worker entry point."""
from __future__ import annotations

import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
import vidsc_audio_copy as audio
import vidsc_copy_job as video


def main():
    parser = video.parser()
    parser.add_argument("--phase", choices=("qualify", "write", "validate"), required=True)
    parser.add_argument("--audio-stream", action="append", required=True)
    parser.add_argument("--manifest-id")
    args = parser.parse_args()
    result = audio.execute(args)
    payload = json.dumps(result, ensure_ascii=False, separators=(",", ":"))
    if len(payload.encode("utf-8")) > 2 * 1024 * 1024:
        result = dict(schemaVersion=1, phase=args.phase, jobId=args.job_id, sourceIdentity=args.source_id,
            generation=args.generation, success=False, error="ReportBudgetExceeded")
        payload = json.dumps(result)
    print(payload)
    return 0 if result["success"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
