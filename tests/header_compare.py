"""Compare two generated headers type by type, ignoring formatting, ordering and comments.

    python tests/header_compare.py <expected.h> <actual.h>

Exits with 1 when any type, base, field or DO_* macro differs.
"""
import difflib
import re
import sys


def load(path):
    """Parse a header into {name: (kind, base, [normalized body lines])}, DO_* macros and forward declarations."""
    text = open(path, encoding="utf-8", errors="replace").read().replace("\r\n", "\n")
    text = re.sub(r"/\*.*?\*/", lambda m: m.group(0) if "Unresolved" in m.group(0) else "", text, flags=re.S)
    types, macros, fwd = {}, [], set()
    for m in re.finditer(r"^\s*(DO_\w+)\((\w+)\)", text, re.M):
        macros.append(f"{m.group(1)}({m.group(2)})")
    for m in re.finditer(r"^\s*(struct|union|enum)\s+(\w+)\s*;", text, re.M):
        fwd.add(m.group(2))
    for m in re.finditer(r"^\s*(struct|union|enum)\s+(\w+)([^{;]*)\{", text, re.M):
        kind, name, base = m.group(1), m.group(2), m.group(3)
        depth, i = 1, m.end()
        while depth and i < len(text):
            depth += {"{": 1, "}": -1}.get(text[i], 0)
            i += 1
        lines = []
        for ln in text[m.end():i - 1].split("\n"):
            ln = re.sub(r"\s+", " ", ln).strip().rstrip(";,").strip()
            if ln and not ln.startswith("#"):
                lines.append(ln)
        types[name] = (kind, re.sub(r"\s+", " ", base).strip(), lines)
    return types, macros, fwd


def compare(expected_path, actual_path, out=sys.stdout):
    """Print a report and return the number of differences."""
    exp, exp_macros, _ = load(expected_path)
    act, act_macros, _ = load(actual_path)
    only_exp = sorted(set(exp) - set(act))
    only_act = sorted(set(act) - set(exp))
    macros_exp = sorted(set(exp_macros) - set(act_macros))
    macros_act = sorted(set(act_macros) - set(exp_macros))
    print(f"types: expected={len(exp)} actual={len(act)} common={len(set(exp) & set(act))}", file=out)
    print(f"only in expected ({len(only_exp)}):", *only_exp, sep="\n  ", file=out)
    print(f"only in actual ({len(only_act)}):", *[f"{act[n][0]} {n} [{len(act[n][2])} lines]" for n in only_act], sep="\n  ", file=out)
    print(f"DO_* macros only in expected ({len(macros_exp)}):", *macros_exp, sep="\n  ", file=out)
    print(f"DO_* macros only in actual ({len(macros_act)}):", *macros_act, sep="\n  ", file=out)
    differing = 0
    for n in sorted(set(exp) & set(act)):
        if exp[n] == act[n]:
            continue
        differing += 1
        print(f"-- {exp[n][0]} {n}", file=out)
        if exp[n][1] != act[n][1]:
            print(f"   base: expected '{exp[n][1]}' / actual '{act[n][1]}'", file=out)
        for d in difflib.unified_diff(exp[n][2], act[n][2], n=0, lineterm=""):
            if not d.startswith(("---", "+++", "@@")):
                print("   " + d, file=out)
    print(f"{differing} differing common types", file=out)
    return len(only_exp) + len(only_act) + len(macros_exp) + len(macros_act) + differing


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    sys.exit(1 if compare(sys.argv[1], sys.argv[2]) else 0)
