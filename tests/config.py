"""Paths and Core sources shared by the test scripts. Each setting can be overridden with an environment variable.

Core's headers are read from Sakura.Suzuran's git history rather than from a working tree or a
local copy, so the tests do not change when Core moves on:
  - UPDATE_COMMIT is Core's commit that applied ApiDiff for the dump in DUMP;
  - its parent holds the header before the update (the regression target);
  - the commit itself holds the expected result.
"""
import os
import subprocess

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(REPO, "tests", ".out")


def _setting(env, default):
    return os.environ.get(env, default)


# Built ApiDiff executable (Debug build by default).
EXE = _setting("APIDIFF_EXE", os.path.join(REPO, "ApiDiff", "bin", "Debug", "net11.0", "win-x64", "ApiDiff.exe"))
# Windows SDK include root, which selects the Windows parse mode.
WINSDK = _setting("APIDIFF_WINSDK", r"C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0")
# Il2CppInspector header of the dump UPDATE_COMMIT was generated from.
DUMP = _setting("APIDIFF_DUMP", r"F:\repos\ios\dump\明日方舟\2.7.71\appdata\il2cpp-types.h")
# Sakura.Suzuran repository and its "fix: apply ApiDiff on 2.7.71." commit.
CORE_REPO = _setting("APIDIFF_CORE_REPO", r"J:\Sakura.Suzuran")
UPDATE_COMMIT = _setting("APIDIFF_UPDATE_COMMIT", "4f36318")
CORE_IL2CPP_DIR = "Sakura.Core/framework/il2cpp"


def core_file(name, rev):
    """Return the bytes of a file in Core's il2cpp directory at the given revision."""
    return subprocess.run(["git", "-C", CORE_REPO, "show", f"{rev}:{CORE_IL2CPP_DIR}/{name}"],
                          check=True, capture_output=True).stdout


def write_core_file(name, rev, directory, as_name=None):
    """Write a Core file at the given revision into directory and return its path."""
    path = os.path.join(directory, as_name or name)
    with open(path, "wb") as file:
        file.write(core_file(name, rev))
    return path
