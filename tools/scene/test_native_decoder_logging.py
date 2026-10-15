"""Bounded real-PyAV regression proof; accepts only an authored synthetic fixture.

The supervisor suppresses native diagnostics and publishes no source paths or
exception text. It kills the worker after 60 seconds, including native hangs.
"""
from __future__ import annotations

import argparse
import json
import logging
from pathlib import Path
import subprocess
import sys
import threading
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parent))
import vidsc_inspection_job as inspection
import vidsc_copy_job as video
import vidsc_audio_copy as audio


def run_checks(source):
    import av

    checks = {}
    report = dict(schemaVersion=1, pyavVersion=av.__version__, checks=checks,
                  initialNativeLevel=av.logging.get_level())
    logger = logging.getLogger("libav")

    def check(name, value):
        checks[name] = bool(value)
        if not value:
            raise AssertionError(name)

    def state():
        return (av.logging.get_level(), av.logging.get_skip_repeated(),
                logger.level, logger.propagate, tuple(logger.handlers))

    def emit(level, message="synthetic native diagnostic"):
        av.logging.log(level, "vidsc_logging_control", message + "\n")

    original = state()
    report["initialSkipRepeated"] = original[1]
    check("defaultNativeLoggingDisabled", original[0] is None)
    try:
        for level_name in ("ERROR", "FATAL", "PANIC"):
            level = getattr(av.logging, level_name)
            with inspection.NativeDecoderLogGate() as gate:
                check(level_name + "ScopeSettings", av.logging.get_level() == av.logging.ERROR
                      and not av.logging.get_skip_repeated()
                      and logger.level == logging.ERROR and not logger.propagate)
                emit(level)
                check(level_name + "MainThreadObserved", gate.failed)
            check(level_name + "MainRestored", state() == original)
            with inspection.NativeDecoderLogGate() as gate:
                worker = threading.Thread(target=emit, args=(level,), daemon=True)
                worker.start()
                worker.join(timeout=3)
                check(level_name + "ThreadFinished", not worker.is_alive())
                check(level_name + "OtherThreadObserved", gate.failed)
            check(level_name + "ThreadRestored", state() == original)

        with inspection.NativeDecoderLogGate() as gate:
            emit(av.logging.WARNING)
            check("warningDoesNotFail", not gate.failed)

        for ordinal in range(2):
            with inspection.NativeDecoderLogGate() as gate:
                emit(av.logging.ERROR, "identical repeated synthetic diagnostic")
                check("repeatedErrorScope" + str(ordinal), gate.failed)

        marker = logging.NullHandler()
        logger.addHandler(marker)
        marked = state()
        try:
            with inspection.NativeDecoderLogGate() as outer:
                outer_state = state()
                with inspection.NativeDecoderLogGate() as inner:
                    emit(av.logging.ERROR)
                    check("nestedBothObserve", inner.failed and outer.failed)
                check("innerRestoresOuterOwnership", state() == outer_state)
                check("existingHandlerPreserved", marker in logger.handlers)
                raise ArithmeticError()
        except ArithmeticError:
            pass
        check("exceptionRestoresNestedState", state() == marked)
        logger.removeHandler(marker)
        marker.close()

        with inspection.NativeDecoderLogGate() as gate:
            check("gateOwnsHandler", isinstance(gate, logging.Handler) and gate in logger.handlers)
            # Handler bookkeeping (filters, locks) is allowed; diagnostic storage
            # must remain a fixed boolean regardless of callback count.
            def retained_shape():
                return {key: type(value).__name__ for key, value in vars(gate).items()}
            before = retained_shape()
            for _ in range(10000):
                emit(av.logging.ERROR, "bounded repeated synthetic diagnostic")
            check("tenThousandErrorsLatched", gate.failed is True)
            check("fixedRetentionShape", retained_shape() == before)
            retained = [value for key, value in vars(gate).items()
                        if key not in ("filters", "previous", "_name")]
            check("noRawDiagnosticCollections", gate.filters == [] and not hasattr(gate, "logs")
                  and not any(isinstance(value, (list, dict, set, tuple, str, bytes)) for value in retained))

        with av.open(str(source)) as container:
            extra = bytes(container.streams.video[0].codec_context.extradata)
        with inspection.NativeDecoderLogGate() as gate:
            decoder = av.CodecContext.create("h264", "r")
            decoder.extradata = extra
            decoder.thread_count = 1
            raised = False
            try:
                decoder.decode(av.Packet(b"\x00\x00\x00\x02\x65\xff"))
                decoder.decode(None)
            except av.FFmpegError:
                raised = True
            check("damagedPacketErrorOrException", gate.failed or raised)
            check("damagedPacketNativeErrorObserved", gate.failed)

        args = SimpleNamespace(input=str(source), job_id="00000000-0000-4000-8000-000000000047",
                               source_id="synthetic-avc-aac-control", generation=47, stream=0,
                               first_packet=12, last_packet=23, start_pts=12288, end_pts=24576,
                               codec="h264", output_media=None, phase="qualify",
                               audio_stream=["1:aac"], manifest_id=None)
        clean_video = video.execute(args)
        check("actualVideoClean", clean_video["success"] and clean_video["error"] is None)
        clean_audio = audio.execute(args)
        check("actualAudioClean", clean_audio["success"] and clean_audio["error"] is None)

        original_qualify = video.qualify
        def injected_qualify(arguments, result):
            original_qualify(arguments, result)
            emit(av.logging.ERROR, "synthetic nested qualification failure")
        video.qualify = injected_qualify
        try:
            failed_video = video.execute(args)
            check("actualVideoRejectsNativeError", not failed_video["success"]
                  and failed_video["error"] == "NativeDecoderError")
            failed_audio = audio.execute(args)
            check("actualAudioRejectsInnerNativeError", not failed_audio["success"]
                  and failed_audio["error"] == "NativeDecoderError")
        finally:
            video.qualify = original_qualify
        check("allScopesRestored", state() == original)
    except Exception as exc:
        report["failureType"] = type(exc).__name__
    report["finalNativeLevel"] = av.logging.get_level()
    report["success"] = bool(checks) and all(checks.values()) and "failureType" not in report
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--worker", action="store_true", help=argparse.SUPPRESS)
    args = parser.parse_args()
    if args.worker:
        try:
            report = run_checks(args.source)
        except Exception as exc:
            report = dict(schemaVersion=1, success=False, failureType=type(exc).__name__)
        args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        return 0 if report["success"] else 1
    if args.output.resolve() == args.source.resolve():
        parser.error("Output must be distinct from the synthetic source")
    # Owned evidence reports must be new, so a stale success cannot survive a
    # worker crash. This also preserves existing files on invocation mistakes.
    with args.output.open("x", encoding="utf-8") as stream:
        stream.write(json.dumps(dict(schemaVersion=1, success=False, workerIncomplete=True)) + "\n")
    try:
        process = subprocess.run([sys.executable, str(Path(__file__).resolve()),
                                  "--source", str(args.source.resolve()),
                                  "--output", str(args.output.resolve()), "--worker"],
                                 timeout=60, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        return 0 if process.returncode == 0 else 1
    except subprocess.TimeoutExpired:
        args.output.write_text(json.dumps(dict(schemaVersion=1, success=False, wallBudgetExceeded=True))
                               + "\n", encoding="utf-8")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
