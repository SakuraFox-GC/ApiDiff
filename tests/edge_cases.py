"""Edge-case suite for ApiDiff: one synthetic Inspector-style input and Core-style target per case.

    python tests/edge_cases.py [name-filter ...]

The input preamble is the dump's IL2CPP internal types plus a few system types, and the target
preamble is Core's header at UPDATE_COMMIT up to `namespace app {`, so both parse like the real files.
Every case is also run a second time with its output as the target: the output must compile
and reproduce itself. Cases are written to tests/.out/edge/<name>/. Paths come from tests/config.py.
"""
import os
import re
import shutil
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from config import DUMP, EXE, OUT, UPDATE_COMMIT, WINSDK as SDK, core_file  # noqa: E402
from header_compare import load  # noqa: E402

# ---------------------------------------------------------------- fixtures
def dump_slices():
    lines = open(DUMP, encoding="utf-8", errors="replace").read().split("\n")
    pre = "\n".join(lines[:2481])
    wanted = ["Delegate__Fields", "Delegate", "Delegate__Array", "MulticastDelegate__Fields", "MulticastDelegate",
              "Action__Fields", "Action", "String__Fields", "String", "Vector2", "Vector3"]
    found = {}
    i = 2481
    while i < len(lines) and len(found) < len(wanted):
        m = re.match(r"^struct (?:__declspec\(align\(8\)\) )?(\w+) \{", lines[i])
        if m and m.group(1) in wanted and m.group(1) not in found:
            j = i
            while lines[j] != "};":
                j += 1
            found[m.group(1)] = "\n".join(lines[i:j + 1])
            i = j
        i += 1
    return pre + "\n\n" + "\n\n".join(found[n] for n in wanted) + "\n"

INPUT_PRE = dump_slices()
CLASS_H = core_file("il2cpp-class.h", UPDATE_COMMIT)
_t = core_file("il2cpp-types.h", UPDATE_COMMIT).decode("utf-8").replace("\r\n", "\n")
TARGET_PRE = _t[:_t.index("namespace app {")]

def obj(name, fields, base=None, align=False):
    body = ([f"struct {base}__Fields _;"] if base else []) + fields
    a = "__declspec(align(8)) " if align else ""
    return (f"struct {a}{name}__Fields {{\n    " + "\n    ".join(body) + "\n};\n"
            f"struct {name} {{\n    struct {name}__Class *klass;\n    MonitorData *monitor;\n    struct {name}__Fields fields;\n}};\n")

def val(name, fields):
    return f"struct {name} {{\n    " + "\n    ".join(fields) + "\n};\n"

def enum(name, items):
    return (f"enum {name}__Enum {{\n" + "".join(f"    {name}__Enum_{n} = 0x{v:08x},\n" for n, v in items) + "};\n")

def arr(elem, by_ptr=True):
    v = f"struct {elem} *vector[32];" if by_ptr else f"struct {elem} vector[32];"
    return (f"struct {elem}__Array {{\n    struct {elem}__Array__Class *klass;\n    MonitorData *monitor;\n"
            f"    Il2CppArrayBounds *bounds;\n    il2cpp_array_size_t max_length;\n    {v}\n}};\n")

# ---------------------------------------------------------------- cases
# Each case: input (app types), target (namespace body), checks.
#   S: {name: [body lines]}  exact normalized body of a struct/enum
#   B: {name: base}          base clause, e.g. ": Il2CppObject"
#   absent: [names]          types that must not be emitted
#   has / hasnt: [regex]     on the generated header
#   fwd: set                 exact set of forward declarations in the namespace
#   log: [regex]             must appear in the log
#   fail: True               tool must refuse cleanly (target untouched)
#   known_broken: True       the output is expected not to compile (reported as KNOWN)
#   note: str                documented behaviour, not a pass/fail judgement
FOO = "struct Foo : Il2CppObject {\n    int32_t f;\n};\n"
FOO_IN = obj("Foo", ["int32_t f;"])
CASES = {}

def case(name, **kw):
    CASES[name] = kw

# --- field merging
case("field_insert_middle", input=obj("H", ["int32_t a;", "int32_t n;", "int32_t b;"]),
     target="struct H : Il2CppObject {\n    int32_t a;\n    int32_t b;\n};\n",
     S={"H": ["int32_t a", "int32_t n", "int32_t b"]})
case("field_insert_leading", input=obj("H", ["int32_t n;", "int32_t a;", "int32_t b;"]),
     target="struct H : Il2CppObject {\n    int32_t a;\n    int32_t b;\n};\n",
     S={"H": ["int32_t n", "int32_t a", "int32_t b"]})
case("field_trailing_not_restored", input=obj("H", ["int32_t a;", "int32_t b;", "int32_t t1;", "String *t2;"]),
     target="struct H : Il2CppObject {\n    int32_t a;\n    int32_t b;\n};\n",
     S={"H": ["int32_t a", "int32_t b"]})
case("field_empty_body_stays_empty", input=obj("H", ["int32_t a;", "int32_t b;"]),
     target="struct H : Il2CppObject {\n};\n", S={"H": []})
case("field_removed_middle", input=obj("H", ["int32_t a;", "int32_t b;"]),
     target="struct H : Il2CppObject {\n    int32_t a;\n    int32_t x;\n    int32_t b;\n};\n",
     S={"H": ["int32_t a", "int32_t b"]})
case("field_removed_last_with_new_trailing", input=obj("H", ["int32_t a;", "int32_t b;", "int64_t t;"]),
     target="struct H : Il2CppObject {\n    int32_t a;\n    int32_t b;\n    int32_t c;\n};\n",
     S={"H": ["int32_t a", "int32_t b"]},
     note="c no longer exists; t sits after the last kept field so it is treated as trimmed")
case("field_removed_last_same_layout", input=obj("H", ["int32_t a;", "int32_t b;", "int32_t t;"]),
     target="struct H : Il2CppObject {\n    int32_t a;\n    int32_t b;\n    int32_t c;\n};\n",
     S={"H": ["int32_t a", "int32_t b", "int32_t c"]},
     note="same count and size: compared by index, Core's name c is kept for t's slot")
case("field_type_shrunk", input=obj("H", ["uint8_t a;", "int32_t b;"]),
     target="struct H : Il2CppObject {\n    int32_t a;\n    int32_t b;\n};\n",
     S={"H": ["uint8_t a", "int32_t b"]})
case("field_enum_resized", input=enum("Mode", [("A", 0)]) + obj("H", ["int64_t mode;", "int32_t b;"]),
     target="enum Mode__Enum {\n    Mode__Enum_A = 0x00000000,\n};\n\n"
            "struct H : Il2CppObject {\n    Mode__Enum mode;\n    int32_t b;\n};\n",
     S={"H": ["int64_t mode", "int32_t b"]})
case("field_renamed_middle", input=obj("H", ["int32_t a;", "int64_t n2;", "int32_t b;"]),
     target="struct H : Il2CppObject {\n    int32_t a;\n    int32_t n1;\n    int32_t b;\n};\n",
     S={"H": ["int32_t a", "int64_t n2", "int32_t b"]})
case("field_no_name_matches", input=obj("H", ["int32_t x;", "int32_t y;", "int32_t z;"]),
     target="struct H : Il2CppObject {\n    int32_t p;\n    int32_t q;\n};\n",
     S={"H": ["int32_t x", "int32_t y", "int32_t z"]})
case("field_same_layout_rename_keeps_target_name", input=obj("H", ["int32_t a;", "int32_t c;"]),
     target="struct H : Il2CppObject {\n    int32_t a;\n    int32_t b;\n};\n",
     S={"H": ["int32_t a", "int32_t b"]},
     note="same count and size: compared by index, Core's name is kept")
case("field_type_widened", input=obj("H", ["int64_t a;", "int32_t b;"]),
     target="struct H : Il2CppObject {\n    int32_t a;\n    int32_t b;\n};\n",
     S={"H": ["int64_t a", "int32_t b"]})
case("field_enum_type_kept", input=enum("Mode", [("A", 0), ("B", 1)]) + obj("H", ["int32_t mode;", "int32_t b;"]),
     target="enum Mode__Enum {\n    Mode__Enum_A = 0x00000000,\n    Mode__Enum_B = 0x00000001,\n};\n\n"
            "struct H : Il2CppObject {\n    Mode__Enum mode;\n    int32_t b;\n};\n",
     S={"H": ["Mode__Enum mode", "int32_t b"]})
case("field_same_count_reordered", input=FOO_IN + obj("H", ["struct Foo *b;", "int64_t a;"]),
     target=FOO + "\nstruct H : Il2CppObject {\n    int64_t a;\n    Foo *b;\n};\n",
     S={"H": ["Foo *b", "int64_t a"]})
case("field_size_t_kept", input=obj("H", ["size_t s;", "int32_t a;"]),
     target="struct H : Il2CppObject {\n    size_t s;\n    int32_t a;\n};\n",
     S={"H": ["size_t s", "int32_t a"]})

# --- base classes
case("base_chain_child_own_fields", input=obj("Base", ["int32_t a;"]) + obj("Child", ["int32_t n;", "int32_t c;", "int32_t t;"], base="Base"),
     target="struct Base : Il2CppObject {\n    int32_t a;\n};\n\nstruct Child : Base {\n    int32_t c;\n};\n",
     S={"Base": ["int32_t a"], "Child": ["int32_t n", "int32_t c"]})
case("base_private_field_same_name_as_base", input=obj("Base", ["int32_t a;"]) + obj("Child", ["int32_t a;", "int32_t c;"], base="Base"),
     target="struct Base : Il2CppObject {\n    int32_t a;\n};\n\nstruct Child : Base {\n    int32_t c;\n};\n",
     S={"Child": ["int32_t a", "int32_t c"]},
     note="C# allows a private field in the child with the same name as one in the base")
case("base_flattened_boxed_header", input=val("Mod", ["int32_t v;"]) + "struct Mod__Boxed {\n    struct Mod__Boxed__Class *klass;\n    MonitorData *monitor;\n    struct Mod fields;\n};\n",
     target="struct Mod {\n    int32_t v;\n};\n\nstruct Mod__Boxed : Il2CppObject {\n    Mod fields;\n};\n",
     S={"Mod__Boxed": ["Mod fields"], "Mod": ["int32_t v"]})
case("wrapper_struct_new_field_inserted", input=val("Wrap", ["int32_t m_value;"]) + obj("H", ["int32_t a;", "struct Wrap w;", "int32_t b;"]),
     target="struct H : Il2CppObject {\n    int32_t a;\n    int32_t b;\n};\n",
     S={"H": ["int32_t a", "Wrap w", "int32_t b"], "Wrap": ["int32_t m_value"]},
     note="a new by-value field inserts the wrapper struct rather than collapsing it")
case("wrapper_struct_matched_field_collapsed", input=val("Wrap", ["int32_t m_value;"]) + obj("H", ["int32_t a;", "struct Wrap w;", "int64_t b;"]),
     target="struct H : Il2CppObject {\n    int32_t a;\n    int32_t w;\n    int32_t b;\n};\n",
     S={"H": ["int32_t a", "int32_t w", "int64_t b"]}, absent=["Wrap"])
case("wrapper_struct_defined_in_target_kept", input=val("Wrap", ["int32_t m_value;"]) + obj("H", ["int32_t a;", "struct Wrap w;", "struct Wrap n;", "int32_t b;"]),
     target="struct Wrap {\n    int32_t m_value;\n};\n\nstruct H : Il2CppObject {\n    int32_t a;\n    Wrap w;\n    int32_t b;\n};\n",
     S={"H": ["int32_t a", "Wrap w", "Wrap n", "int32_t b"], "Wrap": ["int32_t m_value"]})
case("base_not_in_input_hierarchy", input=obj("Base", ["int32_t a;"]) + obj("H", ["int32_t a;", "int32_t n;", "int32_t c;"]),
     target="struct Base : Il2CppObject {\n    int32_t a;\n};\n\nstruct H : Base {\n    int32_t c;\n};\n",
     S={"H": ["int32_t n", "int32_t c"]})

# --- pointers and arrays
TGT3 = FOO + "\nstruct Bar : Il2CppObject {\n    int32_t r;\n};\n\n"
IN3 = FOO_IN + obj("Bar", ["int32_t r;"])
case("ptr_retarget_to_known_type", input=IN3 + obj("H", ["struct Bar *p;", "int32_t a;"]),
     target=TGT3 + "struct H : Il2CppObject {\n    Foo *p;\n    int32_t a;\n};\n",
     S={"H": ["Bar *p", "int32_t a"]})
case("ptr_curated_kept", input=IN3 + obj("Unk", ["int32_t u;"]) + obj("H", ["struct Unk *p;", "int32_t a;"]),
     target=TGT3 + "struct H : Il2CppObject {\n    Foo *p;\n    int32_t a;\n};\n",
     S={"H": ["Foo *p", "int32_t a"]}, absent=["Unk"])
case("ptr_depth_changed", input=IN3 + obj("H", ["struct Foo **p;", "int32_t a;"]),
     target=TGT3 + "struct H : Il2CppObject {\n    Foo *p;\n    int32_t a;\n};\n",
     S={"H": ["Foo **p", "int32_t a"]})
case("ptr_new_field_unknown_class", input=IN3 + obj("Unk", ["int32_t u;"]) + obj("H", ["int32_t a;", "struct Unk *n;", "int32_t b;"]),
     target=TGT3 + "struct H : Il2CppObject {\n    int32_t a;\n    int32_t b;\n};\n",
     S={"H": ["int32_t a", "Il2CppObject *n", "int32_t b"]}, absent=["Unk"])
case("ptr_new_field_nearest_ancestor", input=IN3 + obj("Derived", ["int32_t d;"], base="Foo") + obj("H", ["int32_t a;", "struct Derived *n;", "int32_t b;"]),
     target=TGT3 + "struct H : Il2CppObject {\n    int32_t a;\n    int32_t b;\n};\n",
     S={"H": ["int32_t a", "Foo *n", "int32_t b"]}, absent=["Derived"])
case("ptr_new_field_class_suffix", input=IN3 + obj("H", ["int32_t a;", "struct Foo__Class *n;", "int32_t b;"]),
     target=TGT3 + "struct H : Il2CppObject {\n    int32_t a;\n    int32_t b;\n};\n",
     S={"H": ["int32_t a", "Il2CppClass *n", "int32_t b"]})
case("ptr_new_field_generic_action", input=IN3 + obj("Action_1_Int32_", [], base="MulticastDelegate") + obj("H", ["int32_t a;", "struct Action_1_Int32_ *cb;", "int32_t b;"]),
     target=TGT3 + "struct H : Il2CppObject {\n    int32_t a;\n    int32_t b;\n};\n",
     S={"H": ["int32_t a", "Action *cb", "int32_t b"]})
case("array_new_field_known_element", input=IN3 + arr("Foo") + obj("H", ["int32_t a;", "struct Foo__Array *items;", "int32_t b;"]),
     target=TGT3 + "struct H : Il2CppObject {\n    int32_t a;\n    int32_t b;\n};\n",
     S={"H": ["int32_t a", "Foo__Array *items", "int32_t b"]}, has=[r"DO_ARRAY_DEFINE_PTR\(Foo\)"])
case("array_new_field_element_defined_later", input=IN3 + obj("Late", ["int32_t l;"]) + arr("Late") + obj("H", ["int32_t a;", "struct Late__Array *items;", "int32_t b;"]),
     target=TGT3 + "struct H : Il2CppObject {\n    int32_t a;\n    int32_t b;\n};\n\nstruct Late : Il2CppObject {\n    int32_t l;\n};\n",
     S={"H": ["int32_t a", "Late__Array *items", "int32_t b"]}, has=[r"DO_ARRAY_DEFINE_PTR\(Late\)"])
case("array_new_field_unknown_element", input=IN3 + obj("Unk", ["int32_t u;"]) + arr("Unk") + obj("H", ["int32_t a;", "struct Unk__Array *items;", "int32_t b;"]),
     target=TGT3 + "struct H : Il2CppObject {\n    int32_t a;\n    int32_t b;\n};\n",
     S={"H": ["int32_t a", "Il2CppArray *items", "int32_t b"]}, hasnt=[r"DO_ARRAY_DEFINE\w*\(Unk\)"])
case("valuetype_new_field_inserted", input=val("Pos", ["int32_t x;", "int32_t y;"]) + obj("H", ["int32_t a;", "struct Pos v;", "int32_t b;"]),
     target="struct H : Il2CppObject {\n    int32_t a;\n    int32_t b;\n};\n",
     S={"Pos": ["int32_t x", "int32_t y"], "H": ["int32_t a", "Pos v", "int32_t b"]},
     has=[r"(?s)struct Pos \{.*struct H : Il2CppObject \{"])
case("valuetype_inserted_points_back_to_owner", input=val("Pos", ["struct H *owner;", "int32_t y;"]) + obj("H", ["int32_t a;", "struct Pos v;", "int32_t b;"]),
     target="struct H : Il2CppObject {\n    int32_t a;\n    int32_t b;\n};\n",
     S={"Pos": ["H *owner", "int32_t y"], "H": ["int32_t a", "Pos v", "int32_t b"]}, fwd={"H"})

# --- enums
case("enum_complete_gets_new_item", input=enum("E", [("A", 0), ("B", 1), ("C", 2), ("N", 3)]),
     target="enum E__Enum {\n    E__Enum_A = 0x00000000,\n    E__Enum_B = 0x00000001,\n    E__Enum_C = 0x00000002,\n};\n",
     S={"E__Enum": ["E__Enum_A = 0x00000000", "E__Enum_B = 0x00000001", "E__Enum_C = 0x00000002", "E__Enum_N = 0x00000003"]})
case("enum_curated_values_refreshed", input=enum("E", [("A", 0), ("B", 5), ("C", 2), ("D", 3), ("F", 9), ("G", 4)]),
     target="enum E__Enum {\n    E__Enum_B = 0x00000001,\n    E__Enum_F = 0x00000006,\n};\n",
     S={"E__Enum": ["E__Enum_B = 0x00000005", "E__Enum_F = 0x00000009"]}, log=[r"Keeping curated enum E__Enum"])
case("enum_exactly_half_is_full", input=enum("E", [("A", 0), ("B", 1), ("C", 2), ("D", 3)]),
     target="enum E__Enum {\n    E__Enum_A = 0x00000000,\n    E__Enum_B = 0x00000001,\n};\n",
     S={"E__Enum": ["E__Enum_A = 0x00000000", "E__Enum_B = 0x00000001", "E__Enum_C = 0x00000002", "E__Enum_D = 0x00000003"]},
     note="2 of 4 is not less than half, so the enum is treated as complete")
case("enum_curated_item_removed", input=enum("E", [("A", 0), ("B", 1), ("C", 2), ("D", 3), ("F", 4), ("G", 5)]),
     target="enum E__Enum {\n    E__Enum_B = 0x00000001,\n    E__Enum_X = 0x00000007,\n};\n",
     S={"E__Enum": ["E__Enum_B = 0x00000001"]}, log=[r"Dropping 1 items of E__Enum"])
case("enum_opaque_kept", input=enum("E", [("A", 0), ("B", 1)]),
     target="enum E__Enum : int32_t;\n\nstruct H : Il2CppObject {\n    E__Enum e;\n};\n",
     extra_input=obj("H", ["int32_t e;"]), has=[r"enum E__Enum : int32_t;"], hasnt=[r"E__Enum_A"])
case("enum_no_item_matches", input=enum("E", [("A", 0), ("B", 1), ("C", 2)]),
     target="enum E__Enum {\n    Old_E__Enum_A = 0x00000000,\n};\n",
     S={"E__Enum": ["E__Enum_A = 0x00000000", "E__Enum_B = 0x00000001", "E__Enum_C = 0x00000002"]})

# --- forward declarations / ordering
case("fwd_mutual_pointers", input=obj("A", ["struct B *b;"]) + obj("B", ["struct A *a;"]),
     target="struct B;\n\nstruct A : Il2CppObject {\n    B *b;\n};\n\nstruct B : Il2CppObject {\n    A *a;\n};\n",
     S={"A": ["B *b"], "B": ["A *a"]}, fwd={"B"})
case("fwd_self_pointer", input=obj("A", ["struct A *next;"]),
     target="struct A : Il2CppObject {\n    A *next;\n};\n", S={"A": ["A *next"]}, fwd=set())
case("fwd_opaque_pointee", input=obj("A", ["struct Opq *o;"]) + obj("Opq", ["int32_t x;"]),
     target="struct A : Il2CppObject {\n    struct Opq *o;\n};\n", S={"A": ["struct Opq *o"]}, fwd=set())

# --- ignore pragmas
case("pragma_struct_pinned", input=obj("H", ["int32_t a;", "int32_t n;", "int32_t b;"]),
     target="#pragma apidiff push ignore\nstruct H : Il2CppObject {\n    int32_t a;\n    int32_t b;\n};\n#pragma apidiff pop ignore\n",
     S={"H": ["int32_t a", "int32_t b"]}, has=[r"#pragma apidiff push ignore\s*\n\s*struct H"])
case("pragma_field_pinned", input=obj("H", ["int32_t a;", "int32_t b;", "int32_t n;", "int32_t c;"]),
     target="struct H : Il2CppObject {\n    int32_t a;\n#pragma apidiff push ignore\n    int64_t custom;\n#pragma apidiff pop ignore\n    int32_t c;\n};\n",
     S={"H": ["int32_t a", "int64_t custom", "int32_t c"]}, has=[r"push ignore\s*\n\s*int64_t custom;\s*\n\s*#pragma apidiff pop ignore"])

case("pragma_field_pinned_first_and_last", input=obj("H", ["int32_t x;", "int32_t a;", "int32_t y;"]),
     target="struct H : Il2CppObject {\n#pragma apidiff push ignore\n    int64_t head;\n#pragma apidiff pop ignore\n    int32_t a;\n"
            "#pragma apidiff push ignore\n    int32_t tail;\n#pragma apidiff pop ignore\n};\n",
     S={"H": ["int64_t head", "int32_t a", "int32_t tail"]})
case("pragma_field_pinned_matching_name", input=obj("H", ["int32_t a;", "int64_t b;", "int32_t c;"]),
     target="struct H : Il2CppObject {\n    int32_t a;\n#pragma apidiff push ignore\n    int32_t b;\n#pragma apidiff pop ignore\n    int32_t c;\n};\n",
     S={"H": ["int32_t a", "int32_t b", "int32_t c"]})

# --- macros, deep hierarchies, removed types
LIST_IN = (FOO_IN + arr("Foo") + obj("List_1_Torappu_Foo_", ["struct Foo__Array *_items;", "int32_t _size;", "int32_t _version;", "struct Object *_syncRoot;"], align=True))
case("list_macro_roundtrip", input=LIST_IN + obj("H", ["struct List_1_Torappu_Foo_ *list;", "int32_t a;", "int32_t n;", "int32_t b;"]),
     target=FOO + "\nDO_LIST_DEFINE(Foo)\n\nstruct H : Il2CppObject {\n    List_1_Foo *list;\n    int32_t a;\n    int32_t b;\n};\n",
     S={"H": ["List_1_Foo *list", "int32_t a", "int32_t n", "int32_t b"]}, has=[r"\nDO_LIST_DEFINE\(Foo\)\n"],
     hasnt=[r"struct List_1_Foo \{", r"DO_ARRAY_DEFINE\w*\(Foo\)"])
case("deep_hierarchy_three_levels", input=obj("A", ["int32_t a;"]) + obj("B", ["int32_t b;"], base="A") + obj("C", ["int32_t n;", "int32_t c;"], base="B"),
     target="struct A : Il2CppObject {\n    int32_t a;\n};\n\nstruct B : A {\n    int32_t b;\n};\n\nstruct C : B {\n    int32_t c;\n};\n",
     S={"A": ["int32_t a"], "B": ["int32_t b"], "C": ["int32_t n", "int32_t c"]})
case("deep_hierarchy_skipped_level", input=obj("A", ["int32_t a;"]) + obj("B", ["int32_t b;"], base="A") + obj("C", ["int32_t c;"], base="B"),
     target="struct A : Il2CppObject {\n    int32_t a;\n};\n\nstruct C : A {\n    int32_t b;\n    int32_t c;\n};\n",
     S={"C": ["int32_t b", "int32_t c"]},
     note="Core may skip an intermediate base; its fields stay inlined in the child")
case("incomplete_input_declaration_not_matched", input=obj("H", ["struct Foo *p;", "int32_t a;"]),
     target=FOO + "\nstruct H : Il2CppObject {\n    Foo *p;\n    int32_t a;\n};\n",
     S={"H": ["Foo *p", "int32_t a"]}, absent=["Foo"], known_broken=True,
     log=[r"\[Error\] BuildTypeModel : Can not find struct Foo in the input, dropping it\.", r"1 declarations not found in the input were dropped: Foo\."],
     note="by policy a struct missing from the input is dropped; H still points at it and needs fixing by hand")

# --- failures
case("fail_no_app_namespace", input=obj("H", ["int32_t a;"]), target=None,
     raw_target=TARGET_PRE + "struct H {\n    int32_t a;\n};\n", fail=True)
case("fail_target_does_not_compile", input=obj("H", ["int32_t a;"]),
     target="struct H : Il2CppObject {\n    Missing *m;\n};\n", fail=True)
case("class_missing_from_input", input=obj("H", ["int32_t a;"]),
     target="struct H : Il2CppObject {\n    int32_t a;\n};\n\nstruct Gone : Il2CppObject {\n    int32_t g;\n};\n",
     S={"H": ["int32_t a"]}, absent=["Gone"],
     log=[r"\[Error\] BuildTypeModel : Can not find struct Gone in the input, dropping it\.", r"1 declarations not found in the input were dropped: Gone\."])
case("class_missing_referenced_by_value", input=obj("H", ["int32_t a;", "int32_t b;"]),
     target="struct Gone {\n    int32_t g;\n};\n\nstruct H : Il2CppObject {\n    int32_t a;\n    Gone v;\n    int32_t b;\n};\n",
     S={"H": ["int32_t a", "int32_t b"]}, absent=["Gone"], log=[r"Can not find struct Gone in the input, dropping it"])
case("class_missing_referenced_before_definition", input=obj("H", ["struct Gone *p;"]),
     target="struct Gone;\n\nstruct H : Il2CppObject {\n    Gone *p;\n};\n\nstruct Gone : Il2CppObject {\n    int32_t g;\n};\n",
     S={"H": ["Gone *p"]}, absent=["Gone"], fwd=set(), known_broken=True,
     note="by policy Gone is dropped; H still points at it and needs fixing by hand")
case("enum_missing_from_input", input=obj("H", ["int32_t a;"]),
     target="enum Gone__Enum {\n    Gone__Enum_A = 0x00000000,\n};\n\nstruct H : Il2CppObject {\n    int32_t a;\n};\n",
     S={"Gone__Enum": ["Gone__Enum_A = 0x00000000"]}, log=[r"Can not find enum Gone__Enum in the input, keeping it unchanged"],
     note="unlike structs, a missing enum has always been kept")

# ---------------------------------------------------------------- runner
def run(d, inp, tgt):
    p = subprocess.run([EXE, inp, tgt, SDK, "--yes"], cwd=d, stdin=subprocess.DEVNULL,
                       capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=300)
    return p.returncode, re.sub(r"\x1b\[[0-9;]*m", "", p.stdout + p.stderr)

def fwd_decls(text):
    body = text[text.index("namespace app {"):]
    return set(re.findall(r"^(?:struct|union) (\w+);$", body, re.M))

def check(name, c):
    d = os.path.join(OUT, "edge", name)
    shutil.rmtree(d, ignore_errors=True)
    os.makedirs(d)
    open(os.path.join(d, "il2cpp-class.h"), "wb").write(CLASS_H)
    inp, tgt = os.path.join(d, "input.h"), os.path.join(d, "il2cpp-types.h")
    open(inp, "w", encoding="utf-8").write(INPUT_PRE + "\n" + c["input"] + c.get("extra_input", ""))
    original = c.get("raw_target") or (TARGET_PRE + "namespace app {\n\n" + c["target"] + "\n}\n")
    open(tgt, "w", encoding="utf-8", newline="\r\n").write(original)
    code, log = run(d, inp, tgt)
    open(os.path.join(d, "run.log"), "w", encoding="utf-8").write(log)
    out = open(tgt, encoding="utf-8").read()
    problems = []
    crashed = "baffled" in log
    if c.get("fail"):
        if crashed:
            problems.append("crashed instead of failing cleanly")
        if out.replace("\r\n", "\n") != original:
            problems.append("target was modified")
        return problems
    if crashed or "clang diagnostic push" not in out:
        problems.append("no output written" + (" (crashed: " + log.split("baffled.")[1].strip().split("\n")[0] + ")" if crashed else ""))
        return problems
    types, _, _ = load(tgt)
    for n, body in c.get("S", {}).items():
        got = types.get(n)
        if got is None:
            problems.append(f"{n} missing")
        elif got[2] != body:
            problems.append(f"{n}: expected {body} got {got[2]}")
    for n, base in c.get("B", {}).items():
        if n in types and types[n][1] != base:
            problems.append(f"{n}: base expected '{base}' got '{types[n][1]}'")
    for n in c.get("absent", []):
        if n in types:
            problems.append(f"{n} should not be emitted")
    for r in c.get("has", []):
        if not re.search(r, out):
            problems.append(f"missing /{r}/")
    for r in c.get("hasnt", []):
        if re.search(r, out):
            problems.append(f"unexpected /{r}/")
    for r in c.get("log", []):
        if not re.search(r, log):
            problems.append(f"log lacks /{r}/")
    if "fwd" in c and fwd_decls(out) != c["fwd"]:
        problems.append(f"forward decls {sorted(fwd_decls(out))} != {sorted(c['fwd'])}")
    # second pass: output must compile and regenerate itself
    d2 = os.path.join(d, "pass2")
    os.makedirs(d2)
    open(os.path.join(d2, "il2cpp-class.h"), "wb").write(CLASS_H)
    tgt2 = os.path.join(d2, "il2cpp-types.h")
    shutil.copy(tgt, tgt2)
    _, log2 = run(d2, inp, tgt2)
    open(os.path.join(d2, "run.log"), "w", encoding="utf-8").write(log2)
    if re.search(r"Compilation ended with \d+ errors", log2):
        if not c.get("known_broken"):
            problems.append("pass 2: output does not compile: " + "; ".join(re.findall(r"\[Error\] \w+ : (.*error.*)", log2)[:3]))
    elif c.get("known_broken"):
        problems.append("expected the output not to compile, but it does (update the case)")
    elif open(tgt2, encoding="utf-8").read() != out:
        problems.append("pass 2: output not stable")
    return problems

if __name__ == "__main__":
    only = sys.argv[1:]
    failed = 0
    for name, c in CASES.items():
        if only and not any(o in name for o in only):
            continue
        problems = check(name, c)
        status = ("KNOWN" if c.get("known_broken") else "PASS") if not problems else "FAIL"
        failed += bool(problems)
        print(f"{status}  {name}" + (f"   [note: {c['note']}]" if c.get("note") else ""))
        for p in problems:
            print(f"        - {p}")
    print(f"\n{failed} failing of {len([n for n in CASES if not only or any(o in n for o in only)])}")
