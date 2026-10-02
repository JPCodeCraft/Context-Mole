"""Run one existing benchmark process and retain whole-process Linux resource use.

No build, input changes or quality scoring is performed by this wrapper. Run this
as a fresh Python process and pass the already-built benchmark command after --.
Peak RSS covers the child benchmark process; it is neither system-wide RAM nor
managed allocation, and CPU time includes all of its worker threads.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import resource
import subprocess
import sys
import time


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--workload-note', default='')
    parser.add_argument('--progress-output', type=Path,
                        help='Optional namespace-local child CPU/RSS progress; final resource report remains authoritative.')
    parser.add_argument('command', nargs=argparse.REMAINDER)
    args = parser.parse_args(argv)
    command = args.command[1:] if args.command[:1] == ['--'] else args.command
    if sys.platform != 'linux' or not command:
        parser.error('Linux and a child command after -- are required.')
    if args.output.exists():
        parser.error('Resource output already exists; preserve the previous measurement.')
    before = resource.getrusage(resource.RUSAGE_CHILDREN)
    if before.ru_utime or before.ru_stime or before.ru_maxrss:
        parser.error('Run this wrapper as a fresh Python process with no previous children.')
    started = datetime.now(timezone.utc).isoformat()
    clock = time.perf_counter()
    process = subprocess.Popen(command)
    if args.progress_output:
        args.progress_output.parent.mkdir(parents=True, exist_ok=True)
    while True:
        if args.progress_output:
            progress = {'observed_utc': datetime.now(timezone.utc).isoformat(),
                        'namespace_local_child_pid': process.pid,
                        'elapsed_seconds': time.perf_counter() - clock,
                        'status': 'running',
                        'caveat': 'PID is local to this execution namespace. CPU/RSS are sampled; the completed resource report is authoritative.'}
            try:
                # comm can contain spaces/parentheses; fields following its final ')' have fixed offsets.
                fields = Path(f'/proc/{process.pid}/stat').read_text().rsplit(')', 1)[1].split()
                ticks = os.sysconf('SC_CLK_TCK')
                progress.update(user_cpu_seconds=int(fields[11]) / ticks,
                                system_cpu_seconds=int(fields[12]) / ticks,
                                thread_count=int(fields[17]),
                                rss_kib=int(fields[21]) * os.sysconf('SC_PAGE_SIZE') // 1024)
            except (FileNotFoundError, ProcessLookupError):
                pass
            args.progress_output.write_text(json.dumps(progress, indent=2) + '\n')
        try:
            process.wait(timeout=30)
            break
        except subprocess.TimeoutExpired:
            continue
    elapsed = time.perf_counter() - clock
    usage = resource.getrusage(resource.RUSAGE_CHILDREN)
    report = {
        'version': 1, 'measurement': 'whole_child_process_linux_rusage',
        'started_utc': started, 'completed_utc': datetime.now(timezone.utc).isoformat(),
        'command': command, 'return_code': process.returncode,
        'elapsed_seconds': elapsed, 'user_cpu_seconds': usage.ru_utime,
        'system_cpu_seconds': usage.ru_stime,
        'total_cpu_seconds': usage.ru_utime + usage.ru_stime,
        'peak_rss_kib': usage.ru_maxrss, 'peak_rss_gib': usage.ru_maxrss / (1024 * 1024),
        'major_page_faults': usage.ru_majflt, 'minor_page_faults': usage.ru_minflt,
        'voluntary_context_switches': usage.ru_nvcsw,
        'involuntary_context_switches': usage.ru_nivcsw,
        'workload_note': args.workload_note,
        'caveat': ('One complete child-process observation including startup; no warmed-query claim. '
                   'Linux ru_maxrss is process high-water RSS, not global RAM or managed allocation. '
                   'Avoid simultaneous inference and report any other known contention. '
                   'CPU time includes all threads. Child-process trees would require separate interpretation.')
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + '\n')
    if args.progress_output:
        args.progress_output.write_text(json.dumps({'status': 'completed', 'return_code': process.returncode,
            'final_resource_report': str(args.output), 'elapsed_seconds': elapsed,
            'total_cpu_seconds': usage.ru_utime + usage.ru_stime,
            'peak_rss_kib': usage.ru_maxrss}, indent=2) + '\n')
    return process.returncode if process.returncode >= 0 else 128 - process.returncode


if __name__ == '__main__':
    raise SystemExit(main())
