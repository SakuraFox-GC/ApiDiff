"""Regenerate Core's 2.7.61 header with the 2.7.71 dump and compare it with Core's 2.7.71 header.

    python tests/regression_2771.py

Both headers come from Sakura.Suzuran's git history: the parent of UPDATE_COMMIT is the target,
UPDATE_COMMIT is the expected result (see tests/config.py). This is the reference check for ApiDiff
changes and must report 0 differences. It takes about 2 minutes, most of it parsing the 200 MB dump.
The output is left in tests/.out/regression/.
"""
import os
import re
import shutil
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from config import DUMP, EXE, OUT, UPDATE_COMMIT, WINSDK, write_core_file  # noqa: E402
from header_compare import compare  # noqa: E402


def main():
    work = os.path.join(OUT, "regression")
    shutil.rmtree(work, ignore_errors=True)
    os.makedirs(work)
    target = write_core_file("il2cpp-types.h", f"{UPDATE_COMMIT}^", work)
    write_core_file("il2cpp-class.h", f"{UPDATE_COMMIT}^", work)
    expected = write_core_file("il2cpp-types.h", UPDATE_COMMIT, work, "expected.h")

    started = time.time()
    run = subprocess.run([EXE, DUMP, target, WINSDK, "--yes"], cwd=work, stdin=subprocess.DEVNULL,
                         capture_output=True, text=True, encoding="utf-8", errors="replace")
    log = re.sub(r"\x1b\[[0-9;]*m", "", run.stdout + run.stderr)
    open(os.path.join(work, "run.log"), "w", encoding="utf-8").write(log)
    print(f"ApiDiff exit={run.returncode} in {time.time() - started:.0f}s, log: {os.path.join(work, 'run.log')}")
    errors = [line for line in log.splitlines() if "][Error]" in line or "baffled" in line]
    for line in errors:
        print("  " + line)
    if "clang diagnostic push" not in open(target, encoding="utf-8").read():
        print("FAIL: the target was not regenerated")
        return 1

    differences = compare(expected, target)
    print("PASS" if differences == 0 and not errors else "FAIL")
    return 0 if differences == 0 and not errors else 1


if __name__ == "__main__":
    sys.exit(main())
